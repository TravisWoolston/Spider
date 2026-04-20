using System.Collections.Generic;
using UnityEngine;

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

    [HideInInspector] public int stringId = -1;
    [HideInInspector] public Transform trailingAnchor;

    private readonly List<VerletPoint> points = new List<VerletPoint>();
    private readonly List<Vector2> externalForces = new List<Vector2>();
    private LineRenderer lineRenderer;
    private EdgeCollider2D edgeCollider;
    private Vector2[] colliderPoints = new Vector2[0];

    public int PointCount => points.Count;
    public Vector2 FirstPointPosition => points.Count > 0 ? points[0].position : (Vector2)transform.position;
    public Vector2 LastPointPosition => points.Count > 0 ? points[points.Count - 1].position : (Vector2)transform.position;
    public float TotalLength => GetTotalLength();

    public Vector2 GetPointPosition(int index)
    {
        if (points.Count == 0) return transform.position;
        return points[Mathf.Clamp(index, 0, points.Count - 1)].position;
    }

    void Awake()
    {
        EnsureComponents();
    }

    void FixedUpdate()
    {
        Simulate(Time.fixedDeltaTime);
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

    public void BuildLine(Vector2 start, Vector2 end, int pointCount)
    {
        EnsureComponents();
        points.Clear();
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

            float correctionFactor = (distance - segmentLength) / distance;
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
        points.Add(CreatePoint(position));
        externalForces.Add(Vector2.zero);
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
    }
}
