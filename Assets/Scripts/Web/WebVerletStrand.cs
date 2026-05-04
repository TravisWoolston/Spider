using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Lightweight web strand simulation using Verlet points instead of per-segment
/// Rigidbody2D/HingeJoint2D objects.
/// </summary>
[RequireComponent(typeof(LineRenderer))]
[RequireComponent(typeof(EdgeCollider2D))]
public class WebVerletStrand : MonoBehaviour
{
    private struct VerletPoint
    {
        public Vector2 position;
        public Vector2 previousPosition;
        public Transform anchorTransform;
        public Vector2 anchorLocalPosition;
        public WebVerletStrand anchorStrand;
        public float anchorDistance;
        public bool anchorWorld;
        public Vector2 anchorWorldPosition;

        public bool IsAnchored => anchorTransform != null || anchorStrand != null || anchorWorld;
    }

    private struct NeighborLink
    {
        public WebVerletStrand strand;
        public float localDistance;
        public float remoteDistance;
    }

    private struct ActivePulse
    {
        public int id;
        public float originDistance;
        public float previousDistance;
        public float currentDistance;
        public float direction;
        public float strength;
        public int lightIndex;
        public float createdTime;
    }

    private struct PulseVisualSample
    {
        public float pathDistance;
        public float strength;
        public int lightIndex;
    }

    [Header("Simulation")]
    public float segmentLength = 1f;
    [Min(1)] public int solverIterations = 6;
    [Range(0.8f, 1f)] public float damping = 0.985f;
    public float gravityScale = 1f;
    public float pointMass = 1f;
    public float externalForceScale = 1f;

    [Header("Rendering")]
    public float lineWidth = 0.2f;
    public float colliderEdgeRadius = 0.05f;
    public Color baseColor = Color.white;

    [Header("Web Light")]
    public bool useWebLights = true;
    public Color pulseColor = Color.white;
    public float maxLightIntensity = 20.0f;
    public float pulseTravelSpeed = 35.0f;
    public float pulseDistanceFalloff = 0.035f;
    public float shapeLightLength = 6.0f;
    public float shapeLightWidth = 1.2f;
    public float shapeLightFalloffSize = 1.0f;
    [Range(0f, 1f)] public float shapeLightFalloffIntensity = 0.5f;
    [Min(1)] public int maxActiveShapeLights = 32;

    private float lightDimSpeed = 10.0f;
    private float minPulseStrength = 0.03f;
    private float pulseTrailSpacing = 2.0f;
    private float pulseTrailFalloff = 0.45f;

    [HideInInspector] public int stringId = -1;
    [HideInInspector] public Transform trailingAnchor;

    private readonly List<VerletPoint> points = new List<VerletPoint>();
    private readonly List<float> restLengths = new List<float>();
    private readonly List<Vector2> externalForces = new List<Vector2>();
    private readonly List<NeighborLink> neighborLinks = new List<NeighborLink>();
    private readonly List<ActivePulse> activePulses = new List<ActivePulse>();
    private readonly List<PulseVisualSample> pulseVisualSamples = new List<PulseVisualSample>();
    private readonly List<Light2D> pulseLights = new List<Light2D>();
    private readonly List<Transform> pulseLightTransforms = new List<Transform>();
    private readonly List<Vector3[]> pulseShapePaths = new List<Vector3[]>();
    private LineRenderer lineRenderer;
    private EdgeCollider2D edgeCollider;
    private Light2D pulseLight;
    private Transform pulseLightTransform;
    private Vector2[] colliderPoints = new Vector2[0];
    private static int currentPulseId;
    private int lastPulseId = -1;
    private Vector2 lightAnchorPoint;
    private bool disabledLegacyPulseWaves;
    private bool webLightRuntimeBypassed;
    private int[] allSortingLayerIds;

    public int PointCount => points.Count;
    public Vector2 FirstPointPosition => points.Count > 0 ? points[0].position : (Vector2)transform.position;
    public Vector2 LastPointPosition => points.Count > 0 ? points[points.Count - 1].position : (Vector2)transform.position;
    public float TotalLength => GetTotalLength();

    public Vector2 GetPointPosition(int index)
    {
        if (points.Count == 0) return transform.position;
        return points[Mathf.Clamp(index, 0, points.Count - 1)].position;
    }

    public float GetSegmentRestLength(int segmentIndex)
    {
        return GetRestLength(segmentIndex);
    }

    public void SetSegmentRestLength(int segmentIndex, float length)
    {
        if (segmentIndex < 0 || segmentIndex >= restLengths.Count) return;
        restLengths[segmentIndex] = Mathf.Max(0.01f, length);
    }

    public float GetSegmentCurrentLength(int segmentIndex)
    {
        if (points.Count < 2) return 0f;

        int index = Mathf.Clamp(segmentIndex, 0, points.Count - 2);
        return Vector2.Distance(points[index].position, points[index + 1].position);
    }

    public Vector2 GetPointVelocity(int index)
    {
        if (points.Count == 0) return Vector2.zero;

        int pointIndex = Mathf.Clamp(index, 0, points.Count - 1);
        float dt = Mathf.Max(Time.fixedDeltaTime, 0.0001f);
        return (points[pointIndex].position - points[pointIndex].previousPosition) / dt;
    }

    void Awake()
    {
        webLightRuntimeBypassed = !useWebLights;
        EnsureComponents();
        if (webLightRuntimeBypassed)
        {
            ClearWebLightRuntimeState();
        }
    }

    void FixedUpdate()
    {
        Simulate(Time.fixedDeltaTime);
    }

    void Update()
    {
        if (ShouldBypassWebLightRuntime())
        {
            if (!webLightRuntimeBypassed &&
                (activePulses.Count > 0 || pulseVisualSamples.Count > 0 || pulseLights.Count > 0))
            {
                ClearWebLightRuntimeState();
            }
            return;
        }

        UpdatePulses();
        UpdateLighting();
    }

    void LateUpdate()
    {
        UpdateVisualsAndCollider();
    }

    public void Configure(
        float length,
        int iterations,
        float verletDamping,
        float verletGravityScale,
        float width,
        float edgeRadius)
    {
        EnsureComponents();

        segmentLength = Mathf.Max(0.01f, length);
        solverIterations = Mathf.Max(1, iterations);
        damping = Mathf.Clamp(verletDamping, 0.8f, 1f);
        gravityScale = verletGravityScale;
        lineWidth = Mathf.Max(0.01f, width);
        colliderEdgeRadius = Mathf.Max(0f, edgeRadius);

        lineRenderer.startWidth = lineWidth;
        lineRenderer.endWidth = lineWidth;
        edgeCollider.edgeRadius = colliderEdgeRadius;
    }

    public void ConfigureLighting(
        bool enableLights,
        float maxIntensity,
        Color color,
        float dimSpeed,
        float travelSpeed,
        float distanceFalloff,
        float minimumStrength,
        float shapeLength,
        float shapeWidth,
        float shapeFalloffSize,
        float shapeFalloffIntensity,
        int maxShapeLights,
        float trailSpacing,
        float trailFalloff)
    {
        if (webLightRuntimeBypassed)
        {
            useWebLights = false;
            return;
        }

        useWebLights = enableLights;
        if (!useWebLights)
        {
            ClearWebLightRuntimeState();
            return;
        }

        maxLightIntensity = Mathf.Max(0f, maxIntensity);
        pulseColor = color;
        lightDimSpeed = Mathf.Max(0f, dimSpeed);
        pulseTravelSpeed = Mathf.Max(0.01f, travelSpeed);
        pulseDistanceFalloff = Mathf.Max(0f, distanceFalloff);
        minPulseStrength = Mathf.Max(0.001f, minimumStrength);
        shapeLightLength = Mathf.Max(0.01f, shapeLength);
        shapeLightWidth = Mathf.Max(0.01f, shapeWidth);
        shapeLightFalloffSize = Mathf.Max(0f, shapeFalloffSize);
        shapeLightFalloffIntensity = Mathf.Clamp01(shapeFalloffIntensity);
        maxActiveShapeLights = Mathf.Max(1, maxShapeLights);
        pulseTrailSpacing = Mathf.Max(0f, trailSpacing);
        pulseTrailFalloff = Mathf.Max(0f, trailFalloff);
        RetireLightSlotsAboveLimit();

        for (int i = 0; i < pulseLights.Count; i++)
        {
            ConfigurePulseLight(pulseLights[i]);
        }
    }

    public void DisableWebLightingForRuntime()
    {
        webLightRuntimeBypassed = true;
        useWebLights = false;
        ClearWebLightRuntimeState();
    }

    private bool ShouldBypassWebLightRuntime()
    {
        return webLightRuntimeBypassed || !useWebLights;
    }

    private void ClearWebLightRuntimeState()
    {
        activePulses.Clear();
        pulseVisualSamples.Clear();
        DisablePulseLights();
        DisableExistingPulseLightChildren();
        if (lineRenderer != null)
        {
            SetLineSolidColor(baseColor);
        }
    }

    private void RetireLightSlotsAboveLimit()
    {
        int maxSlots = Mathf.Max(1, maxActiveShapeLights);
        for (int i = 0; i < activePulses.Count; i++)
        {
            ActivePulse pulse = activePulses[i];
            if (pulse.lightIndex < maxSlots) continue;

            pulse.lightIndex = -1;
            activePulses[i] = pulse;
        }

        for (int i = maxSlots; i < pulseLights.Count; i++)
        {
            Light2D light = pulseLights[i];
            if (light == null) continue;

            light.intensity = Mathf.MoveTowards(light.intensity, 0f, lightDimSpeed * Time.deltaTime);
            if (light.intensity <= 0.01f)
            {
                light.intensity = 0f;
                light.enabled = false;
            }
        }
    }

    private void DisableExistingPulseLightChildren()
    {
        for (int i = 0; i < transform.childCount; i++)
        {
            Transform child = transform.GetChild(i);
            if (child == null || !child.name.StartsWith("PulseLight")) continue;

            Light2D light = child.GetComponent<Light2D>();
            if (light == null) continue;

            light.intensity = 0f;
            light.enabled = false;
        }
    }

    public void BuildLine(Vector2 start, Vector2 end, int pointCount)
    {
        EnsureComponents();
        points.Clear();
        restLengths.Clear();
        externalForces.Clear();

        int count = Mathf.Max(2, pointCount);
        for (int i = 0; i < count; i++)
        {
            float t = count == 1 ? 0f : (float)i / (count - 1);
            AddPoint(Vector2.Lerp(start, end, t));
        }

        UpdateVisualsAndCollider();
    }

    public void BuildSinglePoint(Vector2 position)
    {
        EnsureComponents();
        points.Clear();
        restLengths.Clear();
        externalForces.Clear();
        AddPoint(position);
        UpdateVisualsAndCollider();
    }

    public void AppendPoint(Vector2 position)
    {
        AddPoint(position);
        UpdateVisualsAndCollider();
    }

    public void PrependPoint(Vector2 position)
    {
        if (points.Count > 0)
        {
            restLengths.Insert(0, Mathf.Max(0.01f, Vector2.Distance(position, points[0].position)));
        }

        points.Insert(0, CreatePoint(position));
        externalForces.Insert(0, Vector2.zero);
        UpdateVisualsAndCollider();
    }

    public void InsertPointBeforeEnd(Vector2 position)
    {
        if (points.Count == 0)
        {
            AddPoint(position);
        }
        else
        {
            int insertIndex = Mathf.Max(0, points.Count - 1);
            if (points.Count >= 2)
            {
                Vector2 previous = points[insertIndex - 1].position;
                Vector2 end = points[insertIndex].position;
                restLengths[insertIndex - 1] = Mathf.Max(0.01f, Vector2.Distance(previous, position));
                restLengths.Insert(insertIndex, Mathf.Max(0.01f, Vector2.Distance(position, end)));
            }
            else
            {
                restLengths.Add(Mathf.Max(0.01f, Vector2.Distance(points[0].position, position)));
            }

            points.Insert(insertIndex, CreatePoint(position));
            externalForces.Insert(insertIndex, Vector2.zero);
        }

        UpdateVisualsAndCollider();
    }

    public void SetStartAnchor(Collider2D collider, Vector2 worldPoint)
    {
        SetPointAnchor(0, collider, worldPoint);
    }

    public void SetStartWorldAnchor(Vector2 worldPoint)
    {
        SetPointWorldAnchor(0, worldPoint);
    }

    public void SetEndAnchor(Collider2D collider, Vector2 worldPoint)
    {
        SetPointAnchor(points.Count - 1, collider, worldPoint);
    }

    public void SetEndAnchor(Transform anchor)
    {
        if (anchor == null || points.Count == 0) return;
        SetPointAnchor(points.Count - 1, anchor, anchor.position);
    }

    public void SetEndWorldAnchor(Vector2 worldPoint)
    {
        SetPointWorldAnchor(points.Count - 1, worldPoint);
    }

    public void ClearStartAnchor()
    {
        ClearPointAnchor(0);
    }

    public void ClearEndAnchor()
    {
        ClearPointAnchor(points.Count - 1);
    }

    public void ClearTrailingAnchor()
    {
        trailingAnchor = null;
        UpdateVisualsAndCollider();
    }

    public void RegisterAttachmentPoints(float overrideMinDist, int stride = 8)
    {
        if (points.Count == 0) return;

        int step = Mathf.Max(1, stride);
        for (int i = 0; i < points.Count; i++)
        {
            if (i != 0 && i != points.Count - 1 && i % step != 0) continue;
            GlobalAttachmentManager.StoreAttachmentPoint(points[i].position, EstimateNormal(i), transform, overrideMinDist);
        }
    }

    public void MarkSensed(int frame)
    {
        MarkSensedAt(LastPointPosition, frame);
    }

    public void MarkSensedAt(Vector2 worldPoint, int frame)
    {
        if (ShouldBypassWebLightRuntime()) return;

        if (!TryGetClosestDistanceOnPath(worldPoint, out _, out Vector2 closestPoint))
        {
            return;
        }

        lightAnchorPoint = closestPoint;
    }

    public void TriggerPulse()
    {
        TriggerPulse(LastPointPosition);
    }

    public void TriggerPulse(Vector2 worldPoint)
    {
        if (ShouldBypassWebLightRuntime()) return;

        currentPulseId++;
        Pulse(worldPoint, 1.0f, currentPulseId);
    }

    public void Pulse(Vector2 worldPoint, float strength, int pulseId)
    {
        if (ShouldBypassWebLightRuntime()) return;
        if (lastPulseId == pulseId || strength <= 0.01f) return;

        float pathDistance = GetDistanceAtPoint(worldPoint);
        BeginPulse(pathDistance, strength, pulseId);
    }

    private void BeginPulse(float pathDistance, float strength, int pulseId)
    {
        if (ShouldBypassWebLightRuntime()) return;

        lastPulseId = pulseId;
        float clampedDistance = Mathf.Clamp(pathDistance, 0f, GetTotalLength());
        lightAnchorPoint = GetPointAtDistance(clampedDistance);

        activePulses.Add(CreatePulse(clampedDistance, -1f, strength, pulseId));
        activePulses.Add(CreatePulse(clampedDistance, 1f, strength, pulseId));
    }

    private ActivePulse CreatePulse(float originDistance, float direction, float strength, int pulseId)
    {
        return new ActivePulse
        {
            id = pulseId,
            originDistance = originDistance,
            previousDistance = originDistance,
            currentDistance = originDistance,
            direction = Mathf.Sign(direction),
            strength = strength,
            lightIndex = ClaimPulseLightSlot(),
            createdTime = Time.time
        };
    }

    private int ClaimPulseLightSlot()
    {
        int maxSlots = Mathf.Max(1, maxActiveShapeLights);
        int existingSlotCount = Mathf.Min(pulseLights.Count, maxSlots);

        for (int i = 0; i < existingSlotCount; i++)
        {
            if (!IsLightSlotInUse(i))
            {
                return i;
            }
        }

        if (pulseLights.Count < maxSlots)
        {
            EnsurePulseLightCapacity(pulseLights.Count + 1);
            return pulseLights.Count - 1;
        }

        return StealOldestPulseLightSlot();
    }

    private bool IsLightSlotInUse(int lightIndex)
    {
        for (int i = 0; i < activePulses.Count; i++)
        {
            if (activePulses[i].lightIndex == lightIndex)
            {
                return true;
            }
        }

        return false;
    }

    private int StealOldestPulseLightSlot()
    {
        int oldestPulseIndex = -1;
        float oldestTime = float.MaxValue;

        for (int i = 0; i < activePulses.Count; i++)
        {
            ActivePulse pulse = activePulses[i];
            if (pulse.lightIndex < 0 || pulse.createdTime >= oldestTime) continue;

            oldestTime = pulse.createdTime;
            oldestPulseIndex = i;
        }

        if (oldestPulseIndex < 0)
        {
            return -1;
        }

        ActivePulse oldestPulse = activePulses[oldestPulseIndex];
        int stolenLightIndex = oldestPulse.lightIndex;
        oldestPulse.lightIndex = -1;
        oldestPulse.strength = 0f;
        activePulses[oldestPulseIndex] = oldestPulse;
        return stolenLightIndex;
    }

    public float GetDistanceAtPoint(Vector2 worldPoint)
    {
        TryGetClosestDistanceOnPath(worldPoint, out float pathDistance, out _);
        return pathDistance;
    }

    public Vector2 GetPointAtDistance(float pathDistance)
    {
        if (points.Count == 0) return transform.position;
        if (points.Count == 1) return points[0].position;

        float remaining = Mathf.Clamp(pathDistance, 0f, GetTotalLength());
        for (int i = 0; i < points.Count - 1; i++)
        {
            Vector2 a = points[i].position;
            Vector2 b = points[i + 1].position;
            float segmentDistance = Vector2.Distance(a, b);
            if (segmentDistance <= 0.0001f) continue;

            if (remaining <= segmentDistance)
            {
                return Vector2.Lerp(a, b, remaining / segmentDistance);
            }

            remaining -= segmentDistance;
        }

        return LastPointPosition;
    }

    public Vector2 GetNormalAtDistance(float pathDistance)
    {
        if (points.Count < 2) return Vector2.up;

        float remaining = Mathf.Clamp(pathDistance, 0f, GetTotalLength());
        for (int i = 0; i < points.Count - 1; i++)
        {
            Vector2 tangent = points[i + 1].position - points[i].position;
            float segmentDistance = tangent.magnitude;
            if (segmentDistance <= 0.0001f) continue;

            if (remaining <= segmentDistance)
            {
                tangent /= segmentDistance;
                return new Vector2(-tangent.y, tangent.x);
            }

            remaining -= segmentDistance;
        }

        return EstimateNormal(points.Count - 1);
    }

    public void AddForceAtDistance(float pathDistance, Vector2 force, float radius)
    {
        AddForceAtPosition(GetPointAtDistance(pathDistance), force, radius);
    }

    public void AddForceAtPosition(Vector2 worldPoint, Vector2 force, float radius)
    {
        if (points.Count == 0 || force.sqrMagnitude < 0.000001f) return;

        EnsureExternalForceCapacity();
        float influenceRadius = Mathf.Max(0.001f, radius);
        float radiusSqr = influenceRadius * influenceRadius;
        int nearestIndex = 0;
        float nearestSqr = float.MaxValue;
        bool applied = false;

        for (int i = 0; i < points.Count; i++)
        {
            float sqr = Vector2.SqrMagnitude(points[i].position - worldPoint);
            if (sqr < nearestSqr)
            {
                nearestSqr = sqr;
                nearestIndex = i;
            }

            if (sqr > radiusSqr) continue;

            float distance = Mathf.Sqrt(sqr);
            float weight = 1f - Mathf.Clamp01(distance / influenceRadius);
            externalForces[i] += force * weight;
            applied = true;
        }

        if (!applied)
        {
            externalForces[nearestIndex] += force;
        }
    }

    private void Simulate(float dt)
    {
        if (points.Count == 0) return;
        EnsureExternalForceCapacity();

        ApplyAnchors();

        float inverseMass = 1f / Mathf.Max(0.001f, pointMass);
        for (int i = 0; i < points.Count; i++)
        {
            VerletPoint point = points[i];
            Vector2 force = externalForces[i];
            externalForces[i] = Vector2.zero;

            if (point.IsAnchored) continue;

            Vector2 velocity = (point.position - point.previousPosition) * damping;
            Vector2 acceleration = Physics2D.gravity * gravityScale + force * externalForceScale * inverseMass;
            point.previousPosition = point.position;
            point.position += velocity + acceleration * dt * dt;
            points[i] = point;
        }

        for (int i = 0; i < solverIterations; i++)
        {
            ApplyDistanceConstraints();
            ApplyAnchors();
        }
    }

    private void ApplyDistanceConstraints()
    {
        for (int i = 0; i < points.Count - 1; i++)
        {
            VerletPoint a = points[i];
            VerletPoint b = points[i + 1];
            Vector2 delta = b.position - a.position;
            float distance = delta.magnitude;
            if (distance < 0.0001f) continue;

            float correctionFactor = (distance - GetRestLength(i)) / distance;
            if (a.IsAnchored && b.IsAnchored)
            {
                continue;
            }
            if (a.IsAnchored)
            {
                b.position -= delta * correctionFactor;
            }
            else if (b.IsAnchored)
            {
                a.position += delta * correctionFactor;
            }
            else
            {
                Vector2 correction = delta * (correctionFactor * 0.5f);
                a.position += correction;
                b.position -= correction;
            }

            points[i] = a;
            points[i + 1] = b;
        }
    }

    private void ApplyAnchors()
    {
        for (int i = 0; i < points.Count; i++)
        {
            VerletPoint point = points[i];
            if (!point.IsAnchored) continue;

            Vector2 anchoredPosition = point.anchorWorld
                ? point.anchorWorldPosition
                : point.anchorStrand != null
                    ? point.anchorStrand.GetPointAtDistance(point.anchorDistance)
                    : point.anchorTransform.TransformPoint(point.anchorLocalPosition);
            point.position = anchoredPosition;
            point.previousPosition = anchoredPosition;
            points[i] = point;
        }
    }

    private void SetPointAnchor(int index, Collider2D collider, Vector2 worldPoint)
    {
        if (collider == null) return;

        WebVerletStrand strand = collider.GetComponent<WebVerletStrand>();
        if (strand != null && strand != this)
        {
            SetPointStrandAnchor(index, strand, worldPoint);
            return;
        }

        Transform anchor = collider.attachedRigidbody != null ? collider.attachedRigidbody.transform : collider.transform;
        SetPointAnchor(index, anchor, worldPoint);
    }

    private void SetPointAnchor(int index, Transform anchor, Vector2 worldPoint)
    {
        if (index < 0 || index >= points.Count || anchor == null) return;

        VerletPoint point = points[index];
        point.position = worldPoint;
        point.previousPosition = worldPoint;
        point.anchorTransform = anchor;
        point.anchorLocalPosition = anchor.InverseTransformPoint(worldPoint);
        point.anchorStrand = null;
        point.anchorDistance = 0f;
        point.anchorWorld = false;
        point.anchorWorldPosition = Vector2.zero;
        points[index] = point;
    }

    private void RegisterNeighbor(WebVerletStrand other, float localDistance, float remoteDistance)
    {
        if (other == null || other == this) return;

        float mergeDistance = Mathf.Max(0.25f, segmentLength * 0.5f);
        for (int i = 0; i < neighborLinks.Count; i++)
        {
            NeighborLink link = neighborLinks[i];
            if (link.strand != other) continue;
            if (Mathf.Abs(link.localDistance - localDistance) > mergeDistance ||
                Mathf.Abs(link.remoteDistance - remoteDistance) > mergeDistance)
            {
                continue;
            }

            link.localDistance = localDistance;
            link.remoteDistance = remoteDistance;
            neighborLinks[i] = link;
            return;
        }

        neighborLinks.Add(new NeighborLink
        {
            strand = other,
            localDistance = localDistance,
            remoteDistance = remoteDistance
        });
    }

    private void SetPointStrandAnchor(int index, WebVerletStrand strand, Vector2 worldPoint)
    {
        if (index < 0 || index >= points.Count || strand == null) return;

        float distance = strand.GetDistanceAtPoint(worldPoint);
        Vector2 anchoredPosition = strand.GetPointAtDistance(distance);
        VerletPoint point = points[index];
        point.position = anchoredPosition;
        point.previousPosition = anchoredPosition;
        point.anchorTransform = null;
        point.anchorLocalPosition = Vector2.zero;
        point.anchorStrand = strand;
        point.anchorDistance = distance;
        point.anchorWorld = false;
        point.anchorWorldPosition = Vector2.zero;
        points[index] = point;

        float localDistance = GetDistanceAtPoint(anchoredPosition);
        RegisterNeighbor(strand, localDistance, distance);
        strand.RegisterNeighbor(this, distance, localDistance);
    }

    private void SetPointWorldAnchor(int index, Vector2 worldPoint)
    {
        if (index < 0 || index >= points.Count) return;

        VerletPoint point = points[index];
        point.position = worldPoint;
        point.previousPosition = worldPoint;
        point.anchorTransform = null;
        point.anchorLocalPosition = Vector2.zero;
        point.anchorStrand = null;
        point.anchorDistance = 0f;
        point.anchorWorld = true;
        point.anchorWorldPosition = worldPoint;
        points[index] = point;
    }

    private void ClearPointAnchor(int index)
    {
        if (index < 0 || index >= points.Count) return;

        VerletPoint point = points[index];
        point.anchorTransform = null;
        point.anchorLocalPosition = Vector2.zero;
        point.anchorStrand = null;
        point.anchorDistance = 0f;
        point.anchorWorld = false;
        point.anchorWorldPosition = Vector2.zero;
        points[index] = point;
    }

    private void AddPoint(Vector2 position)
    {
        if (points.Count > 0)
        {
            restLengths.Add(Mathf.Max(0.01f, Vector2.Distance(points[points.Count - 1].position, position)));
        }

        points.Add(CreatePoint(position));
        externalForces.Add(Vector2.zero);
    }

    private float GetRestLength(int segmentIndex)
    {
        if (segmentIndex >= 0 && segmentIndex < restLengths.Count)
            return restLengths[segmentIndex];

        return segmentLength;
    }

    private VerletPoint CreatePoint(Vector2 position)
    {
        return new VerletPoint
        {
            position = position,
            previousPosition = position,
            anchorTransform = null,
            anchorLocalPosition = Vector2.zero,
            anchorStrand = null,
            anchorDistance = 0f,
            anchorWorld = false,
            anchorWorldPosition = Vector2.zero
        };
    }

    private Vector2 EstimateNormal(int index)
    {
        if (points.Count < 2) return Vector2.up;

        Vector2 tangent;
        if (index <= 0)
            tangent = points[1].position - points[0].position;
        else if (index >= points.Count - 1)
            tangent = points[points.Count - 1].position - points[points.Count - 2].position;
        else
            tangent = points[index + 1].position - points[index - 1].position;

        if (tangent.sqrMagnitude < 0.0001f) return Vector2.up;
        tangent.Normalize();
        return new Vector2(-tangent.y, tangent.x);
    }

    private float GetTotalLength()
    {
        float length = 0f;
        for (int i = 0; i < points.Count - 1; i++)
        {
            length += Vector2.Distance(points[i].position, points[i + 1].position);
        }
        return length;
    }

    private bool TryGetClosestDistanceOnPath(Vector2 worldPoint, out float pathDistance, out Vector2 closestPoint)
    {
        pathDistance = 0f;
        closestPoint = points.Count > 0 ? points[0].position : (Vector2)transform.position;
        if (points.Count == 0) return false;
        if (points.Count == 1) return true;

        float bestSqr = float.MaxValue;
        float travelled = 0f;
        for (int i = 0; i < points.Count - 1; i++)
        {
            Vector2 a = points[i].position;
            Vector2 b = points[i + 1].position;
            Vector2 segment = b - a;
            float lengthSqr = segment.sqrMagnitude;
            if (lengthSqr <= 0.000001f) continue;

            float t = Mathf.Clamp01(Vector2.Dot(worldPoint - a, segment) / lengthSqr);
            Vector2 candidate = a + segment * t;
            float sqr = Vector2.SqrMagnitude(worldPoint - candidate);
            if (sqr < bestSqr)
            {
                bestSqr = sqr;
                closestPoint = candidate;
                pathDistance = travelled + Mathf.Sqrt(lengthSqr) * t;
            }

            travelled += Mathf.Sqrt(lengthSqr);
        }

        return true;
    }

    private Vector2 GetClosestPointOnPath(Vector2 worldPoint)
    {
        TryGetClosestDistanceOnPath(worldPoint, out _, out Vector2 closestPoint);
        return closestPoint;
    }

    private void UpdatePulses()
    {
        if (activePulses.Count == 0) return;

        float length = GetTotalLength();
        float travel = Mathf.Max(0.01f, pulseTravelSpeed) * Time.deltaTime;
        for (int i = activePulses.Count - 1; i >= 0; i--)
        {
            ActivePulse pulse = activePulses[i];
            pulse.previousDistance = pulse.currentDistance;
            pulse.currentDistance += pulse.direction * travel;

            PropagateCrossedLinks(pulse);

            if (pulse.currentDistance < 0f ||
                pulse.currentDistance > length ||
                GetPulseStrengthAtDistance(pulse) < minPulseStrength)
            {
                activePulses.RemoveAt(i);
                continue;
            }

            activePulses[i] = pulse;
        }
    }

    private void PropagateCrossedLinks(ActivePulse pulse)
    {
        if (neighborLinks.Count == 0) return;

        float from = Mathf.Min(pulse.previousDistance, pulse.currentDistance);
        float to = Mathf.Max(pulse.previousDistance, pulse.currentDistance);
        for (int i = 0; i < neighborLinks.Count; i++)
        {
            NeighborLink link = neighborLinks[i];
            if (link.strand == null) continue;
            if (link.localDistance < from || link.localDistance > to) continue;

            float propagatedStrength = GetPulseStrengthAtDistance(pulse) * 0.96f;
            link.strand.ReceivePulseAtDistance(link.remoteDistance, propagatedStrength, pulse.id);
        }
    }

    private void ReceivePulseAtDistance(float pathDistance, float strength, int pulseId)
    {
        if (ShouldBypassWebLightRuntime()) return;
        if (lastPulseId == pulseId || strength <= 0.01f) return;
        BeginPulse(pathDistance, strength, pulseId);
    }

    private float GetPulseStrengthAtDistance(ActivePulse pulse)
    {
        float distanceTravelled = Mathf.Abs(pulse.currentDistance - pulse.originDistance);
        float falloff = Mathf.Exp(-Mathf.Max(0f, pulseDistanceFalloff) * distanceTravelled);
        return pulse.strength * falloff;
    }

    private void UpdateLighting()
    {
        EnsureComponents();

        if (!useWebLights)
        {
            DisablePulseLights();
            SetLineSolidColor(baseColor);
            return;
        }

        BuildPulseVisualSamples();
        UpdatePulseShapeLights();
        SetLineSolidColor(baseColor);
    }

    private void BuildPulseVisualSamples()
    {
        pulseVisualSamples.Clear();
        if (activePulses.Count == 0) return;

        float length = GetTotalLength();
        if (length <= 0.001f) return;

        int maxSamples = Mathf.Max(1, maxActiveShapeLights);
        for (int i = 0; i < activePulses.Count && pulseVisualSamples.Count < maxSamples; i++)
        {
            ActivePulse pulse = activePulses[i];
            if (pulse.lightIndex < 0) continue;
            if (pulse.lightIndex >= maxSamples)
            {
                pulse.lightIndex = -1;
                activePulses[i] = pulse;
                continue;
            }

            float headStrength = GetPulseStrengthAtDistance(pulse);
            if (headStrength < minPulseStrength) continue;

            if (pulse.currentDistance < 0f || pulse.currentDistance > length) continue;

            pulseVisualSamples.Add(new PulseVisualSample
            {
                pathDistance = pulse.currentDistance,
                strength = headStrength,
                lightIndex = pulse.lightIndex
            });
        }
    }

    private void UpdatePulseShapeLights()
    {
        for (int i = 0; i < pulseVisualSamples.Count; i++)
        {
            PulseVisualSample sample = pulseVisualSamples[i];
            int lightIndex = sample.lightIndex;
            if (lightIndex < 0 ||
                lightIndex >= pulseLights.Count ||
                lightIndex >= pulseLightTransforms.Count ||
                lightIndex >= pulseShapePaths.Count)
            {
                continue;
            }

            Light2D light = pulseLights[lightIndex];
            Transform lightTransform = pulseLightTransforms[lightIndex];
            ConfigurePulseLight(light);
            WritePulseShapeLight(light, lightTransform, pulseShapePaths[lightIndex], sample);

            light.intensity = maxLightIntensity * sample.strength;
            light.color = pulseColor;
            light.enabled = true;
        }

        FadeUnusedPulseLights();
    }

    private void FadeUnusedPulseLights()
    {
        for (int i = 0; i < pulseLights.Count; i++)
        {
            if (IsLightSlotVisible(i)) continue;

            Light2D light = pulseLights[i];
            if (light == null) continue;

            light.intensity = Mathf.MoveTowards(light.intensity, 0f, lightDimSpeed * Time.deltaTime);
            if (light.intensity <= 0.01f)
            {
                light.intensity = 0f;
                light.enabled = false;
            }
        }
    }

    private bool IsLightSlotVisible(int lightIndex)
    {
        for (int i = 0; i < pulseVisualSamples.Count; i++)
        {
            if (pulseVisualSamples[i].lightIndex == lightIndex)
            {
                return true;
            }
        }

        return false;
    }

    private void DisablePulseLights()
    {
        for (int i = 0; i < pulseLights.Count; i++)
        {
            Light2D light = pulseLights[i];
            if (light == null) continue;

            light.intensity = 0f;
            light.enabled = false;
        }
    }

    private void WritePulseShapeLight(Light2D light, Transform lightTransform, Vector3[] shapePath, PulseVisualSample sample)
    {
        if (light == null || lightTransform == null || shapePath == null || shapePath.Length < 10) return;

        float strandLength = GetTotalLength();
        float lightLength = Mathf.Min(Mathf.Max(0.01f, shapeLightLength), Mathf.Max(0.01f, strandLength));
        float halfLength = lightLength * 0.5f;
        float halfWidth = Mathf.Max(0.01f, shapeLightWidth) * 0.5f * Mathf.Lerp(0.5f, 1f, Mathf.Clamp01(sample.strength));

        WriteShapePoint(shapePath, 0, lightTransform, sample.pathDistance - halfLength, 0.08f, halfWidth, strandLength, 1f);
        WriteShapePoint(shapePath, 1, lightTransform, sample.pathDistance - halfLength * 0.5f, 1.0f, halfWidth, strandLength, 1f);
        WriteShapePoint(shapePath, 2, lightTransform, sample.pathDistance, 0.28f, halfWidth, strandLength, 1f);
        WriteShapePoint(shapePath, 3, lightTransform, sample.pathDistance + halfLength * 0.5f, 1.0f, halfWidth, strandLength, 1f);
        WriteShapePoint(shapePath, 4, lightTransform, sample.pathDistance + halfLength, 0.08f, halfWidth, strandLength, 1f);
        WriteShapePoint(shapePath, 5, lightTransform, sample.pathDistance + halfLength, 0.08f, halfWidth, strandLength, -1f);
        WriteShapePoint(shapePath, 6, lightTransform, sample.pathDistance + halfLength * 0.5f, 1.0f, halfWidth, strandLength, -1f);
        WriteShapePoint(shapePath, 7, lightTransform, sample.pathDistance, 0.28f, halfWidth, strandLength, -1f);
        WriteShapePoint(shapePath, 8, lightTransform, sample.pathDistance - halfLength * 0.5f, 1.0f, halfWidth, strandLength, -1f);
        WriteShapePoint(shapePath, 9, lightTransform, sample.pathDistance - halfLength, 0.08f, halfWidth, strandLength, -1f);

        light.SetShapePath(shapePath);
    }

    private void WriteShapePoint(
        Vector3[] shapePath,
        int index,
        Transform lightTransform,
        float pathDistance,
        float widthScale,
        float halfWidth,
        float strandLength,
        float side)
    {
        float clampedDistance = Mathf.Clamp(pathDistance, 0f, strandLength);
        Vector2 center = GetPointAtDistance(clampedDistance);
        Vector2 normal = GetNormalAtDistance(clampedDistance);
        Vector2 worldPoint = center + normal * (halfWidth * widthScale * side);
        shapePath[index] = lightTransform.InverseTransformPoint(worldPoint);
    }

    private void SetLineSolidColor(Color color)
    {
        Gradient gradient = new Gradient();
        gradient.SetKeys(
            new GradientColorKey[]
            {
                new GradientColorKey(color, 0f),
                new GradientColorKey(color, 1f)
            },
            new GradientAlphaKey[]
            {
                new GradientAlphaKey(color.a, 0f),
                new GradientAlphaKey(color.a, 1f)
            }
        );
        lineRenderer.colorGradient = gradient;
    }

    private void UpdateVisualsAndCollider()
    {
        EnsureComponents();

        int displayCount = points.Count + (trailingAnchor != null ? 1 : 0);
        if (displayCount < 2)
        {
            lineRenderer.positionCount = 0;
            edgeCollider.enabled = false;
            return;
        }

        lineRenderer.positionCount = displayCount;
        EnsureColliderPointCapacity(displayCount);

        for (int i = 0; i < points.Count; i++)
        {
            Vector3 worldPoint = points[i].position;
            lineRenderer.SetPosition(i, worldPoint);
            colliderPoints[i] = transform.InverseTransformPoint(worldPoint);
        }

        if (trailingAnchor != null)
        {
            int trailingIndex = displayCount - 1;
            Vector3 worldPoint = trailingAnchor.position;
            lineRenderer.SetPosition(trailingIndex, worldPoint);
            colliderPoints[trailingIndex] = transform.InverseTransformPoint(worldPoint);
        }

        edgeCollider.enabled = true;
        edgeCollider.points = colliderPoints;
    }

    private void EnsureColliderPointCapacity(int count)
    {
        if (colliderPoints.Length == count) return;
        colliderPoints = new Vector2[count];
    }

    private void EnsureExternalForceCapacity()
    {
        while (externalForces.Count < points.Count)
        {
            externalForces.Add(Vector2.zero);
        }

        if (externalForces.Count > points.Count)
        {
            externalForces.RemoveRange(points.Count, externalForces.Count - points.Count);
        }
    }

    private void EnsureComponents()
    {
        if (lineRenderer == null)
        {
            lineRenderer = GetComponent<LineRenderer>();
            lineRenderer.useWorldSpace = true;
            lineRenderer.numCapVertices = 16;
            lineRenderer.numCornerVertices = 16;
            lineRenderer.startColor = baseColor;
            lineRenderer.endColor = baseColor;
            lineRenderer.sortingLayerName = "Web";

            Shader litShader = Shader.Find("Universal Render Pipeline/2D/Sprite-Lit-Default");
            if (litShader != null)
            {
                lineRenderer.material = new Material(litShader);
            }
        }

        if (edgeCollider == null)
        {
            edgeCollider = GetComponent<EdgeCollider2D>();
            edgeCollider.edgeRadius = colliderEdgeRadius;
            edgeCollider.enabled = false;
        }

        DisableLegacyPulseWaveRenderers();
    }

    private void DisableLegacyPulseWaveRenderers()
    {
        if (disabledLegacyPulseWaves) return;
        disabledLegacyPulseWaves = true;

        for (int i = 0; i < transform.childCount; i++)
        {
            Transform child = transform.GetChild(i);
            if (child == null || !child.name.StartsWith("PulseWave")) continue;

            LineRenderer legacyRenderer = child.GetComponent<LineRenderer>();
            if (legacyRenderer != null)
            {
                legacyRenderer.enabled = false;
            }
        }
    }

    private void EnsurePulseLightCapacity(int count)
    {
        int targetCount = Mathf.Max(0, count);
        while (pulseLights.Count < targetCount)
        {
            int index = pulseLights.Count;
            string lightName = index == 0 ? "PulseLight" : "PulseLight_" + index;
            Transform existingLight = transform.Find(lightName);
            Transform lightTransform;
            if (existingLight == null)
            {
                GameObject lightObject = new GameObject(lightName);
                lightObject.layer = gameObject.layer;
                lightObject.transform.SetParent(transform);
                lightObject.transform.position = transform.position;
                lightTransform = lightObject.transform;
            }
            else
            {
                lightTransform = existingLight;
                lightTransform.gameObject.layer = gameObject.layer;
            }

            Light2D light = lightTransform.GetComponent<Light2D>();
            if (light == null)
            {
                light = lightTransform.gameObject.AddComponent<Light2D>();
                light.lightType = Light2D.LightType.Freeform;
            }

            ConfigurePulseLight(light);
            light.intensity = 0f;
            light.enabled = false;

            pulseLights.Add(light);
            pulseLightTransforms.Add(lightTransform);
            pulseShapePaths.Add(new Vector3[10]);

            if (index == 0)
            {
                pulseLight = light;
                pulseLightTransform = lightTransform;
            }
        }
    }

    private void ConfigurePulseLight(Light2D light)
    {
        if (light == null) return;

        light.lightType = Light2D.LightType.Freeform;
        light.shapeLightFalloffSize = shapeLightFalloffSize;
        light.falloffIntensity = shapeLightFalloffIntensity;
        light.color = pulseColor;
        light.shadowsEnabled = false;
        light.volumetricEnabled = false;
        light.overlapOperation = Light2D.OverlapOperation.Additive;
        if (light.targetSortingLayers == null || light.targetSortingLayers.Length != SortingLayer.layers.Length)
        {
            ApplyAllSortingLayers(light);
        }
    }

    private void ApplyAllSortingLayers(Light2D light)
    {
        SortingLayer[] layers = SortingLayer.layers;
        if (allSortingLayerIds == null || allSortingLayerIds.Length != layers.Length)
        {
            allSortingLayerIds = new int[layers.Length];
            for (int i = 0; i < layers.Length; i++)
            {
                allSortingLayerIds[i] = layers[i].id;
            }
        }

        light.targetSortingLayers = allSortingLayerIds;
    }
}
