using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class SpiderWebShooter : MonoBehaviour
{
    public Transform webStartPoint;
    public int maxWebSegments = 80;
    public LayerMask defaultLayer;
    public float segmentLength = 1f;
    public float overlapRadius = 0.2f;
    public int stringId = 0;
    public BoxCollider2D webRb;
    public float maxShootRange = 50f;
    [Range(0.1f, 1.2f)] public float tensionFactor = 1.01f;
    public float rappellingWinchSpeed = 18f;

    [Header("Verlet Lines")]
    [Min(1)] public int verletSolverIterations = 6;
    [Range(0.8f, 1f)] public float verletDamping = 0.985f;
    public float verletGravityScale = 1f;
    public float verletLineWidth = 0.2f;
    public float verletColliderRadius = 0.05f;
    public float verletShotSpeed = 60f;
    public float verletSpiderWeightScale = 1f;
    public float verletSpiderPullStrength = 12f;
    public float verletSpiderWeightRadius = 1.5f;

    [Header("Verlet Web Light")]
    public bool useWebLights = true;
    public Color webLightPulseColor = Color.white;
    public float webLightMaxIntensity = 20.0f;
    public float webLightPulseTravelSpeed = 35.0f;
    public float webLightPulseDistanceFalloff = 0.035f;
    public float webLightShapeLength = 6.0f;
    public float webLightShapeWidth = 1.2f;
    public float webLightShapeFalloffSize = 1.0f;
    [Range(0f, 1f)] public float webLightShapeFalloffIntensity = 0.5f;
    [Min(1)] public int webLightMaxShapeLights = 32;

    [Header("Rappelling")]
    [Range(0.1f, 1f)] public float rappellingFreshSilkSlack = 0.35f;
    [Range(1f, 1.5f)] public float rappellingLiveSilkMax = 1.05f;
    [Range(1f, 1.5f)] public float rappellingSpoolThreshold = 1.0f;
    public float rappellingIdleDescentSpeed = 0f;
    public float rappellingMaxDescentSpeed = 5f;
    public float rappellingBrakeStrength = 10f;
    public float rappellingSpoolCooldown = 0.08f;
    [Range(0.02f, 0.5f)] public float rappellingHarnessSlack = 0.12f;
    public float rappellingEndpointGripStrength = 60f;
    public float rappellingEndpointGripDamping = 8f;

    private float safetyRange;
    private DistanceJoint2D tetherJoint;
    private DistanceJoint2D placementRope;
    private Rigidbody2D placementRopeBody;
    private Transform currentTetherAnchor;
    private bool tetherAnchorUsesWorldOverride;
    private Vector2 tetherAnchorWorldOverride;

    private GameObject activeWebString;
    private WebVerletStrand activeVerlet;
    private readonly List<WebVerletStrand> createdVerletStrands = new List<WebVerletStrand>();
    private SpiderControllerIK spiderController;
    private bool webLightsBypassedForRuntime;
    private bool isHoldingWeb;
    private bool isPlacementMode;
    private bool isAnimatingShot;
    private bool wasRappelling;
    private int currentSegmentCount;
    private Vector2 lastExtrusionPos;
    private float nextSpoolTime;

    public bool IsHoldingWeb => isHoldingWeb;
    public bool IsPlacementMode => isPlacementMode;
    public GameObject ActiveWebString => activeWebString;

    public bool IsRappelling
    {
        get
        {
            if (!isPlacementMode || spiderController == null || spiderController.legs == null || activeWebString == null || activeVerlet == null)
                return false;

            int activeWebFeet = 0;
            int otherFeet = 0;
            foreach (SpiderLegIK leg in spiderController.legs)
            {
                if (leg == null || !leg.isPlanted) continue;

                if (leg.plantedTransform != null && leg.plantedTransform.IsChildOf(activeWebString.transform))
                    activeWebFeet++;
                else
                    otherFeet++;
            }

            return otherFeet == 0 && (activeWebFeet > 0 || CountPlantedFeet() == 0);
        }
    }

    void Awake()
    {
        tetherJoint = gameObject.AddComponent<DistanceJoint2D>();
        Rigidbody2D rb = GetComponent<Rigidbody2D>();
        if (rb != null) rb.bodyType = RigidbodyType2D.Kinematic;

        tetherJoint.enabled = false;
        tetherJoint.autoConfigureDistance = false;
        tetherJoint.maxDistanceOnly = false;
        tetherJoint.distance = 0.1f;
        currentTetherAnchor = GetSpinneret();
    }

    void Start()
    {
        webLightsBypassedForRuntime = !useWebLights;

        if (webRb != null) segmentLength = Mathf.Max(0.01f, webRb.size.x);

        spiderController = GetComponent<SpiderControllerIK>();
        if (spiderController != null)
        {
            safetyRange = spiderController.legExtensionMax;
            if (spiderController.headRb != null)
            {
                placementRopeBody = spiderController.headRb;
                placementRope = placementRopeBody.gameObject.AddComponent<DistanceJoint2D>();
                placementRope.autoConfigureConnectedAnchor = false;
                placementRope.autoConfigureDistance = false;
                placementRope.connectedBody = null;
                placementRope.anchor = Vector2.zero;
                placementRope.connectedAnchor = Vector2.zero;
                placementRope.maxDistanceOnly = true;
                placementRope.distance = segmentLength * tensionFactor;
                placementRope.enabled = false;
            }
        }
    }

    void Update()
    {
        if (Input.GetMouseButtonDown(1))
        {
            if (!isHoldingWeb && !isPlacementMode)
            {
                if (!TryBeginPlacement())
                    StartCoroutine(BeginShot());
            }
            else if (isPlacementMode)
            {
                TryFinalizePlacement();
            }
            else
            {
                FinalizeExtrusion();
            }
        }

        if (isPlacementMode)
            UpdatePlacement();
        else if (isHoldingWeb)
            UpdateHeldShot();

        if (!webLightsBypassedForRuntime)
        {
            ApplyWebLightSettingsToCreatedStrands();
        }
    }

    private IEnumerator BeginShot()
    {
        isHoldingWeb = true;
        isPlacementMode = false;
        isAnimatingShot = true;
        currentSegmentCount = 0;
        activeVerlet = null;
        tetherAnchorUsesWorldOverride = false;

        Vector2 startPos = GetSpinneretPosition();
        Vector2 mousePos = GetMouseWorldPos();
        Vector2 direction = mousePos - startPos;
        if (direction.sqrMagnitude < 0.0001f)
        {
            direction = transform.right;
        }
        direction.Normalize();

        RaycastHit2D hit = FindBestHit(startPos, direction, maxShootRange, true);
        Vector2 endPoint = hit.collider != null ? hit.point : startPos + direction * (maxShootRange * 0.5f);

        activeWebString = CreateWebContainer(hit.collider, "LiveWebString_");
        activeVerlet = AddVerletStrand();

        float totalDistance = Vector2.Distance(startPos, endPoint);
        int targetSegments = Mathf.Clamp(Mathf.CeilToInt(totalDistance / Mathf.Max(0.01f, segmentLength)), 1, maxWebSegments);
        float noseDistance = 0f;
        float shotSpeed = Mathf.Max(segmentLength, verletShotSpeed);

        while (noseDistance < totalDistance && isHoldingWeb && activeVerlet != null)
        {
            noseDistance = Mathf.Min(totalDistance, noseDistance + shotSpeed * Time.deltaTime);
            Vector2 nosePos = startPos + direction * noseDistance;
            int visibleSegments = Mathf.Clamp(Mathf.CeilToInt(noseDistance / Mathf.Max(0.01f, segmentLength)), 1, targetSegments);

            activeVerlet.BuildLine(nosePos, GetHeldAnchorPosition(), visibleSegments + 1);
            activeVerlet.SetStartWorldAnchor(nosePos);
            AnchorHeldEnd();

            currentSegmentCount = visibleSegments;
            lastExtrusionPos = GetHeldAnchorPosition();
            UpdateHeldTether();
            yield return null;
        }

        if (activeVerlet != null)
        {
            activeVerlet.BuildLine(endPoint, GetHeldAnchorPosition(), targetSegments + 1);
            if (hit.collider != null)
                activeVerlet.SetStartAnchor(hit.collider, hit.point);
            else
                activeVerlet.SetStartWorldAnchor(endPoint);

            AnchorHeldEnd();
            currentSegmentCount = targetSegments;
            lastExtrusionPos = GetHeldAnchorPosition();
            UpdateHeldTether();
        }

        isAnimatingShot = false;
    }

    private void UpdateHeldShot()
    {
        if (activeVerlet == null) return;

        AnchorHeldEnd();
        if (!isAnimatingShot)
        {
            float heldThreshold = segmentLength * Mathf.Max(1.05f, tensionFactor);
            TryCommitLiveEnd(true, heldThreshold, Mathf.Max(0.01f, heldThreshold - segmentLength));
        }

        UpdateHeldTether();
        ApplySpiderLoadToActiveVerlet();
    }

    public void FinalizeExtrusion(Collider2D finalAnchor = null, Vector2 finalPoint = default)
    {
        if (!isHoldingWeb) return;

        if (activeVerlet != null)
        {
            Collider2D target = finalAnchor;
            Vector2 targetPoint = finalPoint;
            if (target == null)
            {
                target = FindVerletContact(activeVerlet.LastPointPosition, out targetPoint);
            }

            MergeContainerIfNeeded(target, "LiveWebString");
            activeVerlet.ClearTrailingAnchor();
            if (target != null)
                activeVerlet.SetEndAnchor(target, targetPoint);
            else
                activeVerlet.ClearEndAnchor();

            activeVerlet.RegisterAttachmentPoints(8.0f);
        }

        if (tetherJoint != null) tetherJoint.enabled = false;
        ClearActiveState(false);
    }

    private bool TryBeginPlacement()
    {
        Vector2 startPos = GetSpinneretPosition();
        Vector2 mousePos = GetMouseWorldPos();
        float distToMouse = Vector2.Distance(startPos, mousePos);
        if (distToMouse > safetyRange) return false;

        Vector2 direction = mousePos - startPos;
        if (direction.sqrMagnitude < 0.0001f) return false;
        direction.Normalize();

        RaycastHit2D hit = FindBestHit(startPos, direction, safetyRange, true);
        if (hit.collider == null) return false;

        isPlacementMode = true;
        isHoldingWeb = false;
        isAnimatingShot = false;
        tetherAnchorUsesWorldOverride = false;
        wasRappelling = false;
        currentSegmentCount = 0;
        nextSpoolTime = 0f;

        activeWebString = CreateWebContainer(hit.collider, "PlacedWebString_");
        activeVerlet = AddVerletStrand();

        float initialDistance = Vector2.Distance(hit.point, startPos);
        int initialSegments = Mathf.Clamp(Mathf.CeilToInt(initialDistance / Mathf.Max(0.01f, segmentLength)), 1, maxWebSegments);
        activeVerlet.BuildLine(hit.point, startPos, initialSegments + 1);
        activeVerlet.SetStartAnchor(hit.collider, hit.point);
        activeVerlet.SetEndAnchor(GetSpinneret());
        activeVerlet.ClearTrailingAnchor();

        currentSegmentCount = initialSegments;
        lastExtrusionPos = startPos;
        float placementLeash = GetPlacementLeash();
        ConfigurePlacementRope(
            Mathf.Max(placementLeash, Vector2.Distance(startPos, GetLiveAnchorPosition())),
            true
        );
        return true;
    }

    private void UpdatePlacement()
    {
        if (activeVerlet == null) return;

        bool rappelling = IsRappelling;
        activeVerlet.ClearTrailingAnchor();

        if (rappelling)
        {
            if (!wasRappelling)
                BeginRappelling();

            UpdateRappelling();
        }
        else
        {
            if (wasRappelling)
                EndRappelling();

            activeVerlet.SetEndAnchor(GetSpinneret());
            float placementLeash = GetPlacementLeash();
            ConfigurePlacementRope(placementLeash, true);
            TryCommitLiveEnd(true, placementLeash, Mathf.Max(0.01f, placementLeash - segmentLength));
            UpdatePlacementRopeAnchor();
        }

        wasRappelling = rappelling;
        ApplySpiderLoadToActiveVerlet();
    }

    private void BeginRappelling()
    {
        if (activeVerlet == null) return;

        activeVerlet.ClearEndAnchor();
        float liveRest = Mathf.Clamp(GetLiveRestLength(), GetFreshSilkLength(), GetMaxLiveSilkLength());
        SetLiveRestLength(liveRest);
        ConfigureRappellingHarness();
    }

    private void EndRappelling()
    {
        if (activeVerlet == null) return;

        activeVerlet.SetEndAnchor(GetSpinneret());
        ConfigurePlacementRope(GetPlacementLeash(), true);
    }

    private void UpdateRappelling()
    {
        if (placementRope == null) return;

        float downInput = spiderController != null ? Mathf.Max(0f, -spiderController.inputDirection.y) : 0f;
        float upInput = spiderController != null ? Mathf.Max(0f, spiderController.inputDirection.y) : 0f;
        float desiredFreshLeash = GetFreshSilkLength();
        float maxLiveSilk = GetMaxLiveSilkLength();
        float liveRest = GetLiveRestLength();
        float targetRest = liveRest;

        if (downInput > 0.05f)
        {
            targetRest = maxLiveSilk;
        }
        else if (upInput > 0.05f)
        {
            targetRest = desiredFreshLeash;
        }
        else
        {
            targetRest = Mathf.Min(liveRest, desiredFreshLeash);
        }

        float adjustSpeed = downInput > 0.05f || upInput > 0.05f
            ? rappellingWinchSpeed * Mathf.Max(downInput, upInput)
            : rappellingBrakeStrength * 0.25f;
        SetLiveRestLength(Mathf.MoveTowards(liveRest, targetRest, adjustSpeed * Time.deltaTime));

        ConfigureRappellingHarness();
        ApplyRappellingEndpointGrip();
        ApplyRappellingControl(downInput, upInput);

        float spoolThreshold = segmentLength * Mathf.Max(1f, rappellingSpoolThreshold);
        if (TryCommitLiveEnd(downInput > 0.05f, spoolThreshold, desiredFreshLeash))
        {
            SetLiveRestLength(desiredFreshLeash);
            ConfigureRappellingHarness();
        }
    }

    private void TryFinalizePlacement()
    {
        if (!isPlacementMode || activeVerlet == null) return;

        Vector2 startPos = GetSpinneretPosition();
        Vector2 mousePos = GetMouseWorldPos();
        Vector2 direction = mousePos - startPos;
        if (direction.sqrMagnitude < 0.0001f) return;
        direction.Normalize();

        RaycastHit2D hit = FindBestHit(startPos, direction, safetyRange, false);
        if (hit.collider == null) return;

        MergeContainerIfNeeded(hit.collider, "PlacedWebString");
        activeVerlet.ClearTrailingAnchor();
        activeVerlet.SetEndAnchor(hit.collider, hit.point);
        activeVerlet.RegisterAttachmentPoints(8.0f);

        DisablePlacementRope();
        ClearActiveState(true);
    }

    public void SetTetherAnchor(Transform anchor)
    {
        currentTetherAnchor = anchor;
        tetherAnchorUsesWorldOverride = false;
        UpdateHeldTether();
    }

    public void SetTetherAnchorPosition(Vector2 worldPosition)
    {
        tetherAnchorWorldOverride = worldPosition;
        tetherAnchorUsesWorldOverride = true;
        UpdateHeldTether();
    }

    public Vector2 GetHeldWebAnchorPosition()
    {
        return GetHeldAnchorPosition();
    }

    public Vector2 GetLastSegmentPos()
    {
        if (activeVerlet != null) return activeVerlet.LastPointPosition;
        return GetSpinneretPosition();
    }

    private bool TryCommitLiveEnd(bool canCommit, float threshold, float tailLength)
    {
        if (!canCommit || activeVerlet == null || activeVerlet.PointCount < 2 || currentSegmentCount >= maxWebSegments)
            return false;

        if (Time.time < nextSpoolTime) return false;

        Vector2 previousPoint = activeVerlet.GetPointPosition(activeVerlet.PointCount - 2);
        Vector2 liveEnd = activeVerlet.LastPointPosition;
        Vector2 liveSegment = liveEnd - previousPoint;
        float liveDistance = liveSegment.magnitude;
        if (liveDistance < threshold || liveDistance <= 0.0001f) return false;

        float clampedTail = Mathf.Clamp(tailLength, 0.01f, Mathf.Max(0.01f, liveDistance - 0.01f));
        Vector2 commitPoint = liveEnd - liveSegment / liveDistance * clampedTail;
        activeVerlet.InsertPointBeforeEnd(commitPoint);
        currentSegmentCount++;
        lastExtrusionPos = commitPoint;
        nextSpoolTime = Time.time + Mathf.Max(0f, rappellingSpoolCooldown);
        return true;
    }

    private float GetPlacementLeash()
    {
        return segmentLength * Mathf.Max(1.05f, tensionFactor);
    }

    private float GetFreshSilkLength()
    {
        return Mathf.Max(0.1f, segmentLength * rappellingFreshSilkSlack);
    }

    private float GetMaxLiveSilkLength()
    {
        return Mathf.Max(GetFreshSilkLength(), segmentLength * Mathf.Max(1f, rappellingLiveSilkMax));
    }

    private void ConfigurePlacementRope(float distance, bool maxDistanceOnly)
    {
        if (placementRope == null || placementRopeBody == null || activeVerlet == null) return;

        placementRope.maxDistanceOnly = maxDistanceOnly;
        placementRope.distance = Mathf.Max(0.1f, distance);
        UpdatePlacementRopeAnchor();
        placementRope.enabled = true;
    }

    private void UpdatePlacementRopeAnchor()
    {
        if (placementRope == null || placementRopeBody == null || activeVerlet == null) return;

        Transform spinneret = GetSpinneret();
        placementRope.connectedBody = null;
        placementRope.anchor = placementRopeBody.transform.InverseTransformPoint(spinneret.position);
        placementRope.connectedAnchor = GetLiveAnchorPosition();
        placementRope.enabled = true;
    }

    private void ConfigureRappellingHarness()
    {
        if (placementRope == null || placementRopeBody == null || activeVerlet == null) return;

        Transform spinneret = GetSpinneret();
        placementRope.connectedBody = null;
        placementRope.anchor = placementRopeBody.transform.InverseTransformPoint(spinneret.position);
        placementRope.connectedAnchor = activeVerlet.LastPointPosition;
        placementRope.maxDistanceOnly = true;
        placementRope.distance = Mathf.Max(0.02f, segmentLength * rappellingHarnessSlack);
        placementRope.enabled = true;
    }

    private void DisablePlacementRope()
    {
        if (placementRope == null) return;
        placementRope.maxDistanceOnly = true;
        placementRope.enabled = false;
    }

    private Vector2 GetLiveAnchorPosition()
    {
        if (activeVerlet == null) return GetSpinneretPosition();
        if (activeVerlet.PointCount < 2) return activeVerlet.LastPointPosition;
        return activeVerlet.GetPointPosition(activeVerlet.PointCount - 2);
    }

    private float GetLiveRestLength()
    {
        if (activeVerlet == null || activeVerlet.PointCount < 2) return segmentLength;
        return activeVerlet.GetSegmentRestLength(activeVerlet.PointCount - 2);
    }

    private void SetLiveRestLength(float length)
    {
        if (activeVerlet == null || activeVerlet.PointCount < 2) return;
        activeVerlet.SetSegmentRestLength(activeVerlet.PointCount - 2, length);
    }

    private void UpdateHeldTether()
    {
        if (tetherJoint == null) return;

        if (activeVerlet != null && isHoldingWeb)
        {
            tetherJoint.enabled = true;
            tetherJoint.connectedBody = null;
            tetherJoint.connectedAnchor = activeVerlet.LastPointPosition;
            tetherJoint.anchor = transform.InverseTransformPoint(GetHeldAnchorPosition());
        }
        else
        {
            tetherJoint.enabled = false;
        }
    }

    private void AnchorHeldEnd()
    {
        if (activeVerlet == null) return;

        if (tetherAnchorUsesWorldOverride)
            activeVerlet.SetEndWorldAnchor(tetherAnchorWorldOverride);
        else
            activeVerlet.SetEndAnchor(GetCurrentTetherAnchor());
    }

    private void ApplySpiderLoadToActiveVerlet()
    {
        if (activeVerlet == null || spiderController == null || spiderController.headRb == null) return;

        bool rappelling = isPlacementMode && wasRappelling;
        Vector2 loadPoint = activeVerlet.LastPointPosition;
        if (isPlacementMode && !rappelling)
            loadPoint = GetLiveAnchorPosition();

        Vector2 spinneret = GetSpinneretPosition();
        Vector2 gravityLoad = Physics2D.gravity * spiderController.headRb.mass * verletSpiderWeightScale;
        Vector2 pullLoad = rappelling ? Vector2.zero : (spinneret - loadPoint) * verletSpiderPullStrength;
        activeVerlet.AddForceAtPosition(loadPoint, gravityLoad + pullLoad, Mathf.Max(verletSpiderWeightRadius, segmentLength));
    }

    private void ApplyRappellingEndpointGrip()
    {
        if (activeVerlet == null || activeVerlet.PointCount == 0 || spiderController == null || spiderController.headRb == null) return;

        Rigidbody2D rb = spiderController.headRb;
        Vector2 endpoint = activeVerlet.LastPointPosition;
        Vector2 spinneret = GetSpinneretPosition();
        Vector2 endpointVelocity = activeVerlet.GetPointVelocity(activeVerlet.PointCount - 1);
        Vector2 relativeVelocity = rb.linearVelocity - endpointVelocity;
        Vector2 gripForce =
            (spinneret - endpoint) * Mathf.Max(0f, rappellingEndpointGripStrength) +
            relativeVelocity * Mathf.Max(0f, rappellingEndpointGripDamping);

        activeVerlet.AddForceAtPosition(endpoint, gripForce, Mathf.Max(segmentLength * 0.5f, verletSpiderWeightRadius));
    }

    private void ApplyRappellingControl(float downInput, float upInput)
    {
        if (spiderController == null || spiderController.headRb == null) return;

        Rigidbody2D rb = spiderController.headRb;
        Vector2 gravityDir = Physics2D.gravity.sqrMagnitude > 0.0001f ? Physics2D.gravity.normalized : Vector2.down;
        float currentGravitySpeed = Vector2.Dot(rb.linearVelocity, gravityDir);
        float targetGravitySpeed = Mathf.Max(0f, rappellingIdleDescentSpeed);

        if (downInput > 0.05f)
            targetGravitySpeed = Mathf.Lerp(rappellingIdleDescentSpeed, rappellingMaxDescentSpeed, downInput);
        else if (upInput > 0.05f)
            targetGravitySpeed = -rappellingMaxDescentSpeed * 0.45f * upInput;

        float speedError = targetGravitySpeed - currentGravitySpeed;
        float correction = Mathf.Clamp01(Mathf.Max(0f, rappellingBrakeStrength) * Time.deltaTime);
        rb.AddForce(gravityDir * (speedError * correction * rb.mass), ForceMode2D.Impulse);
    }

    private WebVerletStrand AddVerletStrand()
    {
        int webLayer = LayerMask.NameToLayer("Web");
        if (webLayer >= 0) activeWebString.layer = webLayer;

        WebVerletStrand strand = activeWebString.AddComponent<WebVerletStrand>();
        strand.stringId = stringId;
        strand.Configure(
            segmentLength,
            verletSolverIterations,
            verletDamping,
            verletGravityScale,
            verletLineWidth,
            verletColliderRadius
        );
        if (webLightsBypassedForRuntime)
        {
            strand.DisableWebLightingForRuntime();
        }
        else
        {
            ConfigureStrandLighting(strand);
            createdVerletStrands.Add(strand);
        }

        Collider2D strandCollider = activeWebString.GetComponent<Collider2D>();
        if (strandCollider != null)
        {
            Collider2D[] spiderColliders = GetComponentsInChildren<Collider2D>();
            foreach (Collider2D spiderCollider in spiderColliders)
            {
                if (spiderCollider != null)
                    Physics2D.IgnoreCollision(spiderCollider, strandCollider);
            }
        }

        return strand;
    }

    private void ApplyWebLightSettingsToCreatedStrands()
    {
        if (webLightsBypassedForRuntime) return;

        for (int i = createdVerletStrands.Count - 1; i >= 0; i--)
        {
            WebVerletStrand strand = createdVerletStrands[i];
            if (strand == null)
            {
                createdVerletStrands.RemoveAt(i);
                continue;
            }

            ConfigureStrandLighting(strand);
        }
    }

    private void ConfigureStrandLighting(WebVerletStrand strand)
    {
        if (strand == null) return;
        if (webLightsBypassedForRuntime)
        {
            strand.DisableWebLightingForRuntime();
            return;
        }

        strand.ConfigureLighting(
            useWebLights,
            webLightMaxIntensity,
            webLightPulseColor,
            10.0f,
            webLightPulseTravelSpeed,
            webLightPulseDistanceFalloff,
            0.03f,
            webLightShapeLength,
            webLightShapeWidth,
            webLightShapeFalloffSize,
            webLightShapeFalloffIntensity,
            webLightMaxShapeLights,
            2.0f,
            0.45f
        );
    }

    private GameObject CreateWebContainer(Collider2D hitCollider, string rootPrefix)
    {
        int webLayer = LayerMask.NameToLayer("Web");
        GameObject container = new GameObject(rootPrefix + stringId);
        if (webLayer >= 0) container.layer = webLayer;

        if (hitCollider != null && hitCollider.gameObject.layer == webLayer && hitCollider.transform.parent != null)
        {
            container.name = "Branch_" + stringId;
            container.transform.SetParent(hitCollider.transform.parent);
        }

        return container;
    }

    private void MergeContainerIfNeeded(Collider2D target, string rootPrefix)
    {
        int webLayer = LayerMask.NameToLayer("Web");
        if (activeWebString == null || target == null || target.gameObject.layer != webLayer) return;
        if (!activeWebString.name.StartsWith(rootPrefix)) return;

        activeWebString.name = "Branch_" + stringId;
        if (target.transform.parent != null)
            activeWebString.transform.SetParent(target.transform.parent);
    }

    private Collider2D FindVerletContact(Vector2 position, out Vector2 point)
    {
        point = position;
        int webMask = LayerMask.GetMask("Web");
        Collider2D[] hits = Physics2D.OverlapCircleAll(position, overlapRadius, defaultLayer.value | webMask);
        foreach (Collider2D hit in hits)
        {
            if (hit == null) continue;
            if (activeWebString != null && hit.transform.IsChildOf(activeWebString.transform)) continue;

            point = hit.ClosestPoint(position);
            return hit;
        }

        return null;
    }

    private RaycastHit2D FindBestHit(Vector2 start, Vector2 direction, float range, bool skipNearWeb)
    {
        int webLayer = LayerMask.NameToLayer("Web");
        int webMask = LayerMask.GetMask("Web");
        RaycastHit2D[] hits = Physics2D.RaycastAll(start, direction, range, defaultLayer.value | webMask);

        RaycastHit2D bestHit = new RaycastHit2D();
        float bestDistance = float.MaxValue;
        foreach (RaycastHit2D hit in hits)
        {
            if (hit.collider == null) continue;
            if (activeWebString != null && hit.collider.transform.IsChildOf(activeWebString.transform)) continue;

            float distance = Vector2.Distance(hit.point, start);
            if (skipNearWeb && hit.collider.gameObject.layer == webLayer && distance < safetyRange * 0.3f)
                continue;

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestHit = hit;
            }
        }

        return bestHit;
    }

    private Transform GetSpinneret()
    {
        return webStartPoint != null ? webStartPoint : transform;
    }

    private Vector2 GetSpinneretPosition()
    {
        return GetSpinneret().position;
    }

    private Transform GetCurrentTetherAnchor()
    {
        if (currentTetherAnchor != null) return currentTetherAnchor;
        return GetSpinneret();
    }

    private Vector2 GetHeldAnchorPosition()
    {
        return tetherAnchorUsesWorldOverride ? tetherAnchorWorldOverride : (Vector2)GetCurrentTetherAnchor().position;
    }

    private int CountPlantedFeet()
    {
        if (spiderController == null || spiderController.legs == null) return 0;

        int count = 0;
        foreach (SpiderLegIK leg in spiderController.legs)
        {
            if (leg != null && leg.isPlanted) count++;
        }

        return count;
    }

    private void ClearActiveState(bool wasPlacement)
    {
        if (wasPlacement) DisablePlacementRope();
        if (tetherJoint != null) tetherJoint.enabled = false;

        isHoldingWeb = false;
        isPlacementMode = false;
        isAnimatingShot = false;
        wasRappelling = false;
        tetherAnchorUsesWorldOverride = false;
        currentTetherAnchor = GetSpinneret();
        activeVerlet = null;
        activeWebString = null;
        currentSegmentCount = 0;
        stringId++;
    }

    private Vector2 GetMouseWorldPos()
    {
        if (Camera.main == null) return transform.position;
        if (Camera.main.orthographic)
            return Camera.main.ScreenToWorldPoint(Input.mousePosition);

        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
        Plane worldPlane = new Plane(Vector3.forward, Vector3.zero);
        if (worldPlane.Raycast(ray, out float entry))
            return ray.GetPoint(entry);

        return Camera.main.ScreenToWorldPoint(new Vector3(Input.mousePosition.x, Input.mousePosition.y, 10f));
    }
}
