using UnityEngine;
using System.Collections.Generic;

public class SpiderControllerIK : MonoBehaviour
{
    // ── Enums ──────────────────────────────────────────────────
    public enum GaitType { Urgency, Alternate }
    public enum SolverMode { Analytical, LimbSolver }

    // ── Inspector ──────────────────────────────────────────────
    [Header("IK Solver")]
    public SolverMode solverMode = SolverMode.LimbSolver;
    public bool isAI = false;

    [Header("Legs")]
    public SpiderLegIK[] legs;
    public GaitType activeGait = GaitType.Urgency;
    public bool autoDetectSide = true;
    public int maxDetached = 2;
    public float legExtensionMax = 10f;
    public float legExtensionMin = 1.5f;
    public float footSpacing = 2f;
    public float minStride = 1f;
    public float headSafeArea = 3f;

    [Header("Body")]
    public Rigidbody2D headRb;
    public float bodySmoothing = 10f;
    public float bodyLead = 2f;
    public float bodyHeight = 0f;
    public float defaultBodyHeight = 0f;
    public float bodyHeightMin = -10f;
    public float bodyHeightMax = 10f;
    public float bodyHeightSpeed = 12f;
    public float rotationSpeed = 5f;
    public float stepCooldown = 0.3f;
    public float stepSpeed = 20f;
    public float stepHeight = 2f;
    public LayerMask attachableLayers;

    [Header("Raycasting")]
    public int rayCount = 72;  // Reduced from 100
    public int kneeRayCount = 24;  // Reduced from 40
    public float sightDistance = 15f;
    public float kneeRayScale = 0.6f;

    [Header("Raycasting Performance")]
    [Tooltip("How often to perform full raycasts (in frames). 1 = every frame, 3 = every 3rd frame")]
    public int raycastFrameSkip = 2;
    [Tooltip("Use simpler single raycast instead of RaycastAll")]
    public bool useSimpleRaycasts = true;
    [Tooltip("Maximum attachment points to store per side")]
    public int maxPointsPerSide = 50;

    [Header("Web Physics")]
    public float spiderWeightMult = 1.0f;
    public float webWeightScale => spiderWeightMult;

    [Header("Debug")]
    public bool showDebug = true;

    [Header("Digging")]
    public bool isDigging = false;

    // ── Runtime state ──────────────────────────────────────────
    private float targetBodyHeight = 0f;
    [HideInInspector] public Vector2 inputDirection;
    private Vector2 smoothedInput;
    private Vector2 currentSurfaceNormal = Vector2.up;
    private float groundFactor = 1.0f;

    private List<AttachmentPoint> leftPoints = new(64);
    private List<AttachmentPoint> rightPoints = new(64);
    private Vector2[] restingFootOffsets;
    private int nextLegIndex;
    private int lastSteppedIndex = -1;

    // Zero-allocation buffers for physics queries - REDUCED sizes
    private RaycastHit2D[] hitBuffer = new RaycastHit2D[16];  // Reduced from 100
    private Collider2D[] overlapBuffer = new Collider2D[100]; // Reduced from 2000

    // Frame skip counter
    private int raycastFrameCounter = 0;

    // Cached layer
    private int webLayer;

    // Cached head transform
    private Transform headT;
    private SpiderWebShooter shooter;
    private SpiderLegIK manipulatorLeg;
    private bool isManipulating = false;

    // ───────────────────────────────────────────────────────────
    //  Lifecycle
    // ───────────────────────────────────────────────────────────

    void Start()
    {
        shooter = GetComponent<SpiderWebShooter>();
        webLayer = LayerMask.NameToLayer("Web");

        if (isAI)
        {
            if (legs == null || legs.Length == 0)
                legs = GetComponentsInChildren<SpiderLegIK>();

            if (headRb == null) headRb = GetComponent<Rigidbody2D>();
            if (headRb == null) headRb = GetComponentInChildren<Rigidbody2D>();
        }

        headT = headRb != null ? headRb.transform : transform;

        if (headRb != null)
        {
            headRb.bodyType = RigidbodyType2D.Dynamic;
            headRb.gravityScale = 1f;
            headRb.freezeRotation = true;
        }

        foreach (var leg in legs)
            leg.Initialize(this);

        if (autoDetectSide)
        {
            foreach (var leg in legs)
            {
                string name = leg.gameObject.name.ToUpper();
                bool isRight = name.Contains("LEG R") || name.Contains("LEG_R") || name.Contains("_R") || name.Contains("RIGHT");
                leg.isPhysicallyRight = isRight;
                leg.isLeftSide = isRight;
            }
        }

        PlantAllFeet();
        CaptureRestingPose();

        foreach (var leg in legs)
            leg.Solve();
    }

    void PlantAllFeet()
    {
        Vector2 headPos = headRb != null ? headRb.position : (Vector2)headT.position;

        for (int i = 0; i < legs.Length; i++)
        {
            var leg = legs[i];
            float reach = leg.thighLength + leg.shinLength;

            float sideSign = leg.isPhysicallyRight ? 1f : -1f;
            Vector2 outward = (Vector2)headT.right * sideSign + (Vector2)(-headT.up);
            outward.Normalize();

            RaycastHit2D hit = Physics2D.Raycast(headPos, outward, reach, attachableLayers);

            if (hit.collider == null)
            {
                hit = Physics2D.Raycast(headPos + (Vector2)headT.right * sideSign * reach * 0.5f,
                                        -headT.up, sightDistance, attachableLayers);
            }

            if (hit.collider != null)
            {
                leg.currentFootPos = hit.point;
                leg.targetFootPos = hit.point;
                leg.plantedNormal = hit.normal;
                leg.isPlanted = true;

                GlobalAttachmentManager.StoreAttachmentPoint(hit.point, hit.normal, hit.collider.gameObject.layer == webLayer ? hit.collider.transform : null);
            }
        }
    }

    void Update()
    {
        ReadInput();

        // Frame-skip raycasts for performance
        raycastFrameCounter++;
        bool shouldRaycast = (raycastFrameCounter >= raycastFrameSkip) || (inputDirection.sqrMagnitude > 0.1f);

        if (shouldRaycast)
        {
            raycastFrameCounter = 0;
            PerformRaycasts();
        }

        if (inputDirection.sqrMagnitude > 0.01f)
            smoothedInput = Vector2.MoveTowards(smoothedInput, inputDirection, Time.deltaTime * 5f);
        else
            smoothedInput = Vector2.MoveTowards(smoothedInput, Vector2.zero, Time.deltaTime * 8f);

        foreach (var leg in legs)
            if (leg.isPlanted) leg.plantedTimer += Time.deltaTime;

        if (isDigging) HandleDigging();
        ProcessGait();
        HandleWebManipulation();

        // Web Sensing (Player Only)
        if (!isAI)
        {
            foreach (var leg in legs)
            {
                if (leg.isPlanted && leg.plantedTransform != null)
                {
                    var ws = leg.plantedTransform.GetComponent<WebSegment>();
                    if (ws != null) ws.MarkSensed(Time.frameCount);
                }
            }
        }

        // Reactive Mid-Step Adjustment
        foreach (var leg in legs)
        {
            if (leg.isStepping && leg.stepProgress < 0.6f)
            {
                AttachmentPoint best = GetBestPoint(leg);
                if (best.isValid)
                {
                    float dist = Vector2.Distance(leg.targetFootPos, best.point);
                    if (dist > minStride * 0.5f)
                        leg.UpdateStepTarget(best.point, best.normal, best.collider);
                }
            }
        }
    }

    void FixedUpdate()
    {
        SolveBodyPose();
    }

    void LateUpdate()
    {
        foreach (var leg in legs)
            leg.Solve();
    }

    // ───────────────────────────────────────────────────────────
    //  Input
    // ───────────────────────────────────────────────────────────

    void ReadInput()
    {
        if (!isAI)
        {
            float rawH = Input.GetAxis("Horizontal");
            float rawV = Input.GetAxis("Vertical");
            inputDirection = new Vector2(rawH, rawV);

            if (inputDirection.sqrMagnitude > 1f)
                inputDirection.Normalize();
            else if (inputDirection.sqrMagnitude < 0.01f)
                inputDirection = Vector2.zero;

            float heightInput = 0f;
            if (Input.GetKey(KeyCode.E) || Input.GetKey(KeyCode.LeftShift)) heightInput += 1f;
            if (Input.GetKey(KeyCode.Q) || Input.GetKey(KeyCode.LeftControl)) heightInput -= 1f;
            if (Input.GetKeyDown(KeyCode.R)) isDigging = !isDigging;

            targetBodyHeight += heightInput * bodyHeightSpeed * Time.deltaTime;
        }

        if (smoothedInput.sqrMagnitude > 0.1f)
        {
            float dodgeBias = Vector2.Dot(smoothedInput, currentSurfaceNormal);

            int pointsAhead = 0;
            foreach (var ap in leftPoints)
                if (Vector2.Dot(ap.point - (Vector2)headT.position, smoothedInput.normalized) > 0.5f) pointsAhead++;
            foreach (var ap in rightPoints)
                if (Vector2.Dot(ap.point - (Vector2)headT.position, smoothedInput.normalized) > 0.5f) pointsAhead++;

            if (pointsAhead < 2 && groundFactor < 0.1f)
                targetBodyHeight = Mathf.Lerp(targetBodyHeight, bodyHeightMin, Time.deltaTime * 3f);
            else
                targetBodyHeight = Mathf.MoveTowards(targetBodyHeight, defaultBodyHeight + dodgeBias * 3f, Time.deltaTime * 3f);
        }
        else
        {
            targetBodyHeight = Mathf.MoveTowards(targetBodyHeight, defaultBodyHeight, Time.deltaTime * 2f);
        }

        float clampMin = groundFactor > 0.1f ? 0.2f : bodyHeightMin;

        if (isDigging) targetBodyHeight = bodyHeightMin;

        targetBodyHeight = Mathf.Clamp(targetBodyHeight, clampMin, bodyHeightMax);
        bodyHeight = Mathf.MoveTowards(bodyHeight, targetBodyHeight, bodyHeightSpeed * Time.deltaTime);
    }

    // ───────────────────────────────────────────────────────────
    //  Raycasting - OPTIMIZED
    // ───────────────────────────────────────────────────────────

    void PerformRaycasts()
    {
        leftPoints.Clear();
        rightPoints.Clear();

        Vector2 origin = headRb != null ? headRb.position : (Vector2)headT.position;

        float step = 360f / rayCount;

        if (useSimpleRaycasts)
        {
            // SIMPLE MODE: Single raycast per direction (much faster)
            for (int i = 0; i < rayCount; i++)
            {
                float rad = i * step * Mathf.Deg2Rad;
                Vector2 dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));

                RaycastHit2D hit = Physics2D.Raycast(origin, dir, sightDistance, attachableLayers);

                if (hit.collider != null)
                {
                    float dist = Vector2.Distance(hit.point, origin);
                    if (hit.collider.gameObject.layer == webLayer && dist < 1.0f)
                        continue;

                    float localX = headT.InverseTransformPoint(hit.point).x;
                    var ap = new AttachmentPoint(hit.point, hit.normal, hit.collider);

                    if (localX < 0)
                    {
                        if (leftPoints.Count < maxPointsPerSide)
                            leftPoints.Add(ap);
                    }
                    else
                    {
                        if (rightPoints.Count < maxPointsPerSide)
                            rightPoints.Add(ap);
                    }
                }
            }
        }
        else
        {
            // COMPLEX MODE: RaycastNonAlloc for seeing through webs
            for (int i = 0; i < rayCount; i++)
            {
                float rad = i * step * Mathf.Deg2Rad;
                Vector2 dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));

                int hitCount = Physics2D.RaycastNonAlloc(origin, dir, hitBuffer, sightDistance, attachableLayers);

                if (hitCount > 1)
                {
                    // Sort only if multiple hits
                    for (int n = 1; n < hitCount; n++)
                    {
                        var key = hitBuffer[n];
                        int m = n - 1;
                        while (m >= 0 && hitBuffer[m].distance > key.distance)
                        {
                            hitBuffer[m + 1] = hitBuffer[m];
                            m--;
                        }
                        hitBuffer[m + 1] = key;
                    }
                }

                for (int j = 0; j < hitCount; j++)
                {
                    var hit = hitBuffer[j];
                    if (hit.collider == null) continue;

                    float dist = Vector2.Distance(hit.point, origin);
                    if (hit.collider.gameObject.layer == webLayer && dist < 1.0f)
                        continue;

                    float localX = headT.InverseTransformPoint(hit.point).x;
                    var ap = new AttachmentPoint(hit.point, hit.normal, hit.collider);

                    if (localX < 0)
                    {
                        if (leftPoints.Count < maxPointsPerSide)
                            leftPoints.Add(ap);
                    }
                    else
                    {
                        if (rightPoints.Count < maxPointsPerSide)
                            rightPoints.Add(ap);
                    }

                    if (hit.collider.gameObject.layer != webLayer)
                        break;
                }
            }
        }

        // Knee-based raycasting - ONLY when moving
        if (inputDirection.sqrMagnitude > 0.05f)
        {
            foreach (var leg in legs)
            {
                Vector2 kneeOrigin = leg.GetKneePosition();
                float kneeStep = 360f / kneeRayCount;

                for (int i = 0; i < kneeRayCount; i++)
                {
                    float rad = i * kneeStep * Mathf.Deg2Rad;
                    Vector2 dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));

                    RaycastHit2D hit = Physics2D.Raycast(kneeOrigin, dir, sightDistance * kneeRayScale, attachableLayers);

                    if (hit.collider != null)
                    {
                        float distFromHead = Vector2.Distance(hit.point, origin);
                        bool isWeb = hit.collider.gameObject.layer == webLayer;
                        if (!isWeb && distFromHead < 1.0f)
                            continue;

                        float localX = headT.InverseTransformPoint(hit.point).x;
                        var ap = new AttachmentPoint(hit.point, hit.normal, hit.collider);

                        if (localX < 0)
                        {
                            if (leftPoints.Count < maxPointsPerSide)
                                leftPoints.Add(ap);
                        }
                        else
                        {
                            if (rightPoints.Count < maxPointsPerSide)
                                rightPoints.Add(ap);
                        }
                    }
                }
            }
        }

        // Web overlap detection - REDUCED radius and count
        int overlapCount = Physics2D.OverlapCircleNonAlloc(origin, sightDistance * 0.5f, overlapBuffer, LayerMask.GetMask("Web"));
        int webPointsAdded = 0;
        int maxWebPoints = 30;

        for (int i = 0; i < overlapCount && webPointsAdded < maxWebPoints; i++)
        {
            Collider2D col = overlapBuffer[i];
            Vector2 pL = col.ClosestPoint(origin);
            float localX = headT.InverseTransformPoint(pL).x;
            var ap = new AttachmentPoint(pL, (origin - pL).normalized, col);

            if (localX < 0)
            {
                if (leftPoints.Count < maxPointsPerSide)
                    leftPoints.Add(ap);
            }
            else
            {
                if (rightPoints.Count < maxPointsPerSide)
                    rightPoints.Add(ap);
            }
            webPointsAdded++;
        }
    }

    // ───────────────────────────────────────────────────────────
    //  Resting pose
    // ───────────────────────────────────────────────────────────

    [ContextMenu("Capture Resting Pose")]
    public void CaptureRestingPose()
    {
        if (legs == null) return;
        restingFootOffsets = new Vector2[legs.Length];
        float avgY = 0;
        for (int i = 0; i < legs.Length; i++)
        {
            Vector2 local = headT.InverseTransformPoint(legs[i].currentFootPos);
            avgY += local.y;
            restingFootOffsets[i] = new Vector2(local.x, 0);
        }

        if (legs.Length > 0)
        {
            avgY /= legs.Length;
            defaultBodyHeight = -avgY;
            targetBodyHeight = defaultBodyHeight;
            bodyHeight = defaultBodyHeight;
        }
    }

    // ───────────────────────────────────────────────────────────
    //  Gaits
    // ───────────────────────────────────────────────────────────

    void ProcessGait()
    {
        if (activeGait == GaitType.Urgency) UrgencyGait();
        else AlternateGait();
    }

    void UrgencyGait()
    {
        foreach (var leg in legs)
        {
            if (!leg.isPlanted || leg.isStepping) continue;

            if (leg.GetExtension() > legExtensionMax * 1.05f)
            {
                if (HasValidTarget(leg))
                {
                    StepLeg(leg);
                    lastSteppedIndex = System.Array.IndexOf(legs, leg);
                    return;
                }
            }
        }

        if (DetachedCount() >= maxDetached) return;

        int detachedL = 0, detachedR = 0;
        foreach (var l in legs)
        {
            if (l.isStepping || !l.isPlanted)
            {
                if (l.isPhysicallyRight) detachedR++;
                else detachedL++;
            }
        }

        SpiderLegIK best = null;
        float maxUrg = 1f;

        foreach (var leg in legs)
        {
            if (!leg.isPlanted || leg.isStepping) continue;

            if (leg.isPhysicallyRight && detachedR >= Mathf.CeilToInt(maxDetached / 2f)) continue;
            if (!leg.isPhysicallyRight && detachedL >= Mathf.CeilToInt(maxDetached / 2f)) continue;

            float u = Urgency(leg);
            if (u > maxUrg)
            {
                if (HasValidTarget(leg))
                {
                    maxUrg = u;
                    best = leg;
                }
            }
        }

        if (best != null)
        {
            lastSteppedIndex = System.Array.IndexOf(legs, best);
            StepLeg(best);
        }
    }

    void AlternateGait()
    {
        if (DetachedCount() >= maxDetached) return;

        var leg = legs[nextLegIndex];

        if (leg.isPlanted && !leg.isStepping && Urgency(leg) > 1f)
        {
            StepLeg(leg);
            nextLegIndex = (nextLegIndex + 1) % legs.Length;
        }
    }

    float Urgency(SpiderLegIK leg)
    {
        int idx = System.Array.IndexOf(legs, leg);
        if (idx < 0 || restingFootOffsets == null) return 0f;

        Vector2 avgPlantedCenter = Vector2.zero;
        int plantedCount = 0;
        foreach (var l in legs) { if (l.isPlanted) { avgPlantedCenter += l.currentFootPos; plantedCount++; } }
        if (plantedCount > 0) avgPlantedCenter /= plantedCount;
        else avgPlantedCenter = (Vector2)headT.position;

        float maxReach = leg.thighLength + leg.shinLength;
        Vector2 idealPos = avgPlantedCenter + (Vector2)(headT.rotation * restingFootOffsets[idx])
                         + smoothedInput * maxReach * 0.4f;

        float distFromIdeal = Vector2.Distance(leg.currentFootPos, idealPos);
        float idealUrgency = distFromIdeal / (maxReach * 0.7f);

        float ext = leg.GetExtension() / legExtensionMax;
        float minExt = leg.GetExtension() / legExtensionMin;
        float extensionUrgency = (ext > 0.9f) ? (ext - 0.9f) * 10f : 0f;
        if (minExt < 1.1f) extensionUrgency += (1.1f - minExt) * 2f;

        float rhythmUrgency = 0f;
        if (lastSteppedIndex != -1)
        {
            bool sameSide = legs[lastSteppedIndex].isPhysicallyRight == leg.isPhysicallyRight;
            rhythmUrgency = sameSide ? 0f : 0.4f;
        }

        float positionalUrgency = (distFromIdeal < minStride || smoothedInput.sqrMagnitude < 0.01f) ? 0f : idealUrgency;

        float proximityUrgency = 0f;
        foreach (var other in legs)
        {
            if (other == leg || !other.isPlanted) continue;
            float d = Vector2.Distance(leg.currentFootPos, other.currentFootPos);
            if (d < footSpacing * 2.5f)
                proximityUrgency += 1f - (d / (footSpacing * 2.5f));
        }

        return Mathf.Max(positionalUrgency, extensionUrgency) + proximityUrgency * 0.4f + rhythmUrgency;
    }

    // ───────────────────────────────────────────────────────────
    //  Stepping
    // ───────────────────────────────────────────────────────────

    void StepLeg(SpiderLegIK leg)
    {
        AttachmentPoint best = GetBestPoint(leg);
        if (best.isValid)
            leg.StepTo(best.point, best.normal, best.collider);
    }

    struct AttachmentPoint
    {
        public Vector2 point;
        public Vector2 normal;
        public Collider2D collider;
        public bool isValid;
        public AttachmentPoint(Vector2 p, Vector2 n, Collider2D c) { point = p; normal = n; collider = c; isValid = true; }
    }

    AttachmentPoint GetBestPoint(SpiderLegIK leg)
    {
        int idx = System.Array.IndexOf(legs, leg);
        List<AttachmentPoint> pool = leg.isPhysicallyRight ? rightPoints : leftPoints;
        if (pool.Count == 0) return new AttachmentPoint { isValid = false };

        Vector2 avgPlantedCenter = Vector2.zero;
        int plantedCount = 0;
        foreach (var l in legs) { if (l.isPlanted) { avgPlantedCenter += l.currentFootPos; plantedCount++; } }
        if (plantedCount > 0) avgPlantedCenter /= plantedCount;
        else avgPlantedCenter = (Vector2)headT.position;
        Vector2 headPos = headRb != null ? headRb.position : (Vector2)headT.position;

        float maxReach = leg.thighLength + leg.shinLength;
        Vector2 idealPos = avgPlantedCenter + (Vector2)(headT.rotation * restingFootOffsets[idx])
                         + smoothedInput * maxReach * 0.4f;

        float comfortZone = maxReach * 0.9f;
        AttachmentPoint best = new AttachmentPoint { isValid = false };
        float bestScore = float.MinValue;

        foreach (var ap in pool)
        {
            float distFromHead = Vector2.Distance(headPos, ap.point);
            bool isWeb = ap.collider.gameObject.layer == webLayer;

            if (distFromHead > legExtensionMax || distFromHead < legExtensionMin) continue;

            float effectSafeArea = isWeb ? headSafeArea * 0.5f : headSafeArea;
            if (distFromHead < effectSafeArea) continue;

            if (IsPointTooCloseToOtherFeet(ap.point, leg)) continue;

            if (!leg.isStepping && inputDirection.sqrMagnitude > 0.01f)
            {
                if (Vector2.Distance(leg.currentFootPos, ap.point) < minStride * (isWeb ? 0.5f : 1.0f))
                    continue;
            }

            float localX = headT.InverseTransformPoint(ap.point).x;
            float score = 0f;

            score -= Vector2.Distance(idealPos, ap.point) * 2.0f;

            float sideBias = leg.isPhysicallyRight ? 1f : -1f;
            float sideMargin = isWeb ? -0.5f : 0.1f;
            if (localX * sideBias < sideMargin) continue;

            if (smoothedInput.sqrMagnitude > 0.05f)
            {
                Vector2 stepVec = (ap.point - leg.currentFootPos);
                float progress = Vector2.Dot(stepVec, smoothedInput.normalized);
                if (progress < -minStride * 0.1f) continue;
            }

            float extDiff = Mathf.Abs(distFromHead - comfortZone);
            score -= extDiff * 1.5f;

            float moveDist = Vector2.Distance(leg.currentFootPos, ap.point);
            if (moveDist > maxReach * 1.2f) score -= 10f;

            if (smoothedInput.sqrMagnitude > 0.05f)
            {
                Vector2 shoulderPos = (Vector2)headT.TransformPoint(restingFootOffsets[idx]);
                Vector2 toPointRel = ap.point - shoulderPos;
                Vector2 inputNorm = smoothedInput.normalized;

                float projection = Vector2.Dot(toPointRel, inputNorm);
                score += projection * 5f;
                score += Vector2.Dot(toPointRel.normalized, inputNorm) * 8f;
            }

            score += Mathf.Abs(localX) * 2f;

            if (score > bestScore) { bestScore = score; best = ap; }
        }
        return best;
    }

    bool HasValidTarget(SpiderLegIK leg)
    {
        List<AttachmentPoint> pool = leg.isPhysicallyRight ? rightPoints : leftPoints;
        Vector2 inputNorm = smoothedInput.normalized;
        bool hasInput = smoothedInput.sqrMagnitude > 0.05f;

        foreach (var ap in pool)
        {
            float distFromHead = Vector2.Distance(headRb.position, ap.point);
            bool isWeb = ap.collider.gameObject.layer == webLayer;

            if (distFromHead > legExtensionMax || distFromHead < legExtensionMin) continue;

            float effectSafeArea = isWeb ? headSafeArea * 0.5f : headSafeArea;
            if (distFromHead < effectSafeArea) continue;

            float localX = headT.InverseTransformPoint(ap.point).x;
            float sideBias = leg.isPhysicallyRight ? 1f : -1f;
            float sideMargin = isWeb ? -0.5f : 0.1f;
            if (localX * sideBias < sideMargin) continue;

            if (IsPointTooCloseToOtherFeet(ap.point, leg)) continue;

            float stepDist = Vector2.Distance(leg.currentFootPos, ap.point);
            if (stepDist < minStride * (isWeb ? 0.5f : 1.0f)) continue;

            if (hasInput)
            {
                float progress = Vector2.Dot(ap.point - leg.currentFootPos, inputNorm);
                if (progress < 0.05f) continue;
            }

            return true;
        }
        return false;
    }

    bool IsPointTooCloseToOtherFeet(Vector2 point, SpiderLegIK excludeLeg)
    {
        foreach (var leg in legs)
        {
            if (leg == excludeLeg || !leg.isPlanted) continue;
            if (Vector2.Distance(point, leg.currentFootPos) < footSpacing)
                return true;
        }
        return false;
    }

    // ───────────────────────────────────────────────────────────
    //  Digging Routine
    // ───────────────────────────────────────────────────────────

    void HandleDigging()
    {
        Vector2 mousePos = Camera.main.ScreenToWorldPoint(Input.mousePosition);

        AttachmentPoint bestPoint = new AttachmentPoint { isValid = false };
        float closestDist = 2.0f;

        var pool = new List<AttachmentPoint>(leftPoints);
        pool.AddRange(rightPoints);

        foreach (var ap in pool)
        {
            float d = Vector2.Distance(ap.point, mousePos);
            if (d < closestDist)
            {
                closestDist = d;
                bestPoint = ap;
            }
        }

        if (bestPoint.isValid)
        {
            SpiderLegIK bestLeg = null;
            float legDist = float.MaxValue;

            foreach (var leg in legs)
            {
                if (leg.isStepping) continue;

                int idx = System.Array.IndexOf(legs, leg);
                Vector2 shoulderPos = (Vector2)headT.TransformPoint(restingFootOffsets[idx]);
                float reach = leg.thighLength + leg.shinLength;

                if (Vector2.Distance(bestPoint.point, shoulderPos) < reach * 0.95f)
                {
                    float d = Vector2.Distance(leg.currentFootPos, bestPoint.point);
                    if (d < legDist) { legDist = d; bestLeg = leg; }
                }
            }

            if (bestLeg != null)
            {
                if (bestLeg.isPlanted && legDist < 0.5f)
                {
                    var terrain = bestPoint.collider.GetComponent<DestructibleTerrain>();
                    if (terrain != null)
                    {
                        terrain.MineHole(bestPoint.point, 1.2f);

                        bestLeg.isPlanted = false;
                        bestLeg.currentFootPos += (Vector2)bestPoint.normal * 1.5f;
                    }
                }
                else
                {
                    bestLeg.StepTo(bestPoint.point, bestPoint.normal, bestPoint.collider);
                }
            }
        }
    }

    // ───────────────────────────────────────────────────────────
    //  Body Pose Solver
    // ───────────────────────────────────────────────────────────

    void SolveBodyPose()
    {
        if (restingFootOffsets == null || legs == null) return;

        foreach (var leg in legs) leg.SyncToSurface();

        int planted = 0;
        Vector2 avgFootPos = Vector2.zero;
        Vector2 normalSum = Vector2.zero;

        foreach (var leg in legs)
        {
            if (!leg.isPlanted) continue;

            avgFootPos += leg.currentFootPos;
            normalSum += leg.plantedNormal;

            planted++;
        }

        if (planted == 0)
        {
            headRb.freezeRotation = false;
            return;
        }

        int groundPlanted = 0;
        foreach (var leg in legs)
        {
            if (leg.isPlanted && leg.plantedTransform != null && leg.plantedTransform.gameObject.layer != webLayer)
                groundPlanted++;
        }
        groundFactor = (float)groundPlanted / Mathf.Max(1, planted);

        int maxOnSameStrand = 0;
        var counts = new Dictionary<Collider2D, int>();
        foreach (var leg in legs)
        {
            if (leg.isPlanted && leg.plantedTransform != null)
            {
                Collider2D col = leg.plantedTransform.GetComponent<Collider2D>();
                if (col != null)
                {
                    counts[col] = counts.ContainsKey(col) ? counts[col] + 1 : 1;
                    if (counts[col] > maxOnSameStrand) maxOnSameStrand = counts[col];
                }
            }
        }
        float strandFactor = (float)maxOnSameStrand / Mathf.Max(1, planted);

        Vector2 avgNormal;
        if (normalSum.sqrMagnitude < 0.01f)
            avgNormal = (Vector2)headT.up;
        else
            avgNormal = normalSum.normalized;

        float num = 0, den = 0;
        Vector2 center = (avgFootPos / planted);
        for (int i = 0; i < legs.Length; i++)
        {
            if (!legs[i].isPlanted) continue;
            Vector2 relPos = (Vector2)legs[i].currentFootPos - center;
            float localX = restingFootOffsets[i].x;
            num += localX * relPos.y;
            den += localX * relPos.x;
        }

        float targetAngle;
        if (Mathf.Abs(num) < 0.0001f && Mathf.Abs(den) < 0.0001f)
        {
            targetAngle = Mathf.Atan2(avgNormal.y, avgNormal.x) * Mathf.Rad2Deg - 90f;
        }
        else
        {
            targetAngle = Mathf.Atan2(num, den) * Mathf.Rad2Deg;

            float altAngle = targetAngle + 180f;
            float currentAngle = headT.rotation.eulerAngles.z;

            float diff1 = Mathf.Abs(Mathf.DeltaAngle(currentAngle, targetAngle));
            float diff2 = Mathf.Abs(Mathf.DeltaAngle(currentAngle, altAngle));

            if (groundFactor > 0.3f)
            {
                float normAngle = Mathf.Atan2(avgNormal.y, avgNormal.x) * Mathf.Rad2Deg - 90f;
                if (Mathf.Abs(Mathf.DeltaAngle(normAngle, altAngle)) < Mathf.Abs(Mathf.DeltaAngle(normAngle, targetAngle)))
                    targetAngle = altAngle;
            }
            else if (diff2 < diff1)
            {
                targetAngle = altAngle;
            }
        }

        currentSurfaceNormal = avgNormal;
        Quaternion stableRot = Quaternion.Euler(0, 0, targetAngle);

        Vector2 posSum = Vector2.zero;
        for (int i = 0; i < legs.Length; i++)
        {
            if (!legs[i].isPlanted) continue;
            Vector2 worldOffset = stableRot * (Vector3)restingFootOffsets[i];
            posSum += (legs[i].currentFootPos - worldOffset);
        }

        Vector2 targetPos = (posSum / planted);

        float searchRadius = bodyHeight > 0 ? bodyHeight * 2.0f : 2.0f;
        RaycastHit2D centerHit = Physics2D.Raycast(targetPos + avgNormal * searchRadius, -avgNormal, searchRadius * 2.0f, attachableLayers);
        if (centerHit.collider != null)
        {
            float surfaceProg = Vector2.Dot(centerHit.point, avgNormal);
            float targetProg = Vector2.Dot(targetPos, avgNormal);
            if (targetProg < surfaceProg)
            {
                targetPos += avgNormal * (surfaceProg - targetProg);
            }
        }

        float confidence = Mathf.Max(groundFactor, strandFactor * 0.7f);

        float effectHeight = bodyHeight * Mathf.Lerp(0.2f, 1.0f, confidence);
        float effectLead = bodyLead * Mathf.Lerp(0.4f, 1.0f, confidence);

        targetPos += avgNormal * effectHeight;

        if (smoothedInput.sqrMagnitude > 0.01f)
        {
            targetPos += (Vector2)smoothedInput * effectLead;
        }

        Vector2 toTarget = targetPos - headRb.position;

        float surfaceSmoothing = Mathf.Lerp(bodySmoothing * 0.7f, bodySmoothing, groundFactor);
        float currentSmoothing = (inputDirection.sqrMagnitude > 0.01f) ? surfaceSmoothing : surfaceSmoothing * 0.2f;

        Vector2 gravityCancel = -Physics2D.gravity * headRb.gravityScale;

        headRb.linearVelocity = Vector2.Lerp(headRb.linearVelocity, toTarget * currentSmoothing, Time.fixedDeltaTime * 10f);
        headRb.AddForce(gravityCancel * headRb.mass, ForceMode2D.Force);

        headT.rotation = Quaternion.Slerp(headT.rotation, stableRot, Time.fixedDeltaTime * rotationSpeed);
        headRb.freezeRotation = true;

        if (spiderWeightMult > 0.01f && planted > 0)
        {
            float weightPerLeg = (headRb.mass * Mathf.Abs(Physics2D.gravity.y) * spiderWeightMult * 2.0f) / planted;
            Vector2 weightDir = Physics2D.gravity.normalized;

            foreach (var leg in legs)
            {
                if (leg.isPlanted && leg.plantedTransform != null)
                {
                    Rigidbody2D targetRb = leg.plantedTransform.GetComponentInParent<Rigidbody2D>();
                    if (targetRb != null && targetRb != headRb)
                    {
                        targetRb.AddForceAtPosition(weightDir * weightPerLeg, leg.currentFootPos, ForceMode2D.Force);
                    }
                }
            }
        }
    }

    // ───────────────────────────────────────────────────────────
    //  Helpers
    // ───────────────────────────────────────────────────────────

    int DetachedCount()
    {
        int n = 0;
        foreach (var leg in legs)
            if (leg.isStepping || !leg.isPlanted) n++;
        return n;
    }

    // ───────────────────────────────────────────────────────────
    //  Web Manipulation
    // ───────────────────────────────────────────────────────────

    void HandleWebManipulation()
    {
        if (shooter == null || !shooter.enabled) return;

        Vector2 mousePos = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        float distToHead = Vector2.Distance(mousePos, headT.position);

        if (!isManipulating && distToHead < legExtensionMax && shooter.IsHoldingWeb)
        {
            manipulatorLeg = legs[0];
            isManipulating = true;
            manipulatorLeg.isPlanted = false;

            if (shooter != null) shooter.SetTetherAnchor(manipulatorLeg.foot);
        }

        if (isManipulating)
        {
            manipulatorLeg.currentFootPos = Vector2.Lerp(manipulatorLeg.currentFootPos, mousePos, Time.deltaTime * 20f);
            manipulatorLeg.isPlanted = false;

            Collider2D col = Physics2D.OverlapCircle(manipulatorLeg.currentFootPos, 0.5f, attachableLayers | LayerMask.GetMask("Web"));

            bool shouldStick = false;
            if (col != null)
            {
                if (col.gameObject.layer == webLayer)
                {
                    WebSegment otherWs = col.GetComponent<WebSegment>();
                    if (otherWs != null && otherWs.stringId != shooter.stringId)
                        shouldStick = true;
                }
                else
                {
                    shouldStick = true;
                }

                if (Input.GetMouseButtonDown(0))
                    shouldStick = true;
            }

            if (shouldStick)
            {
                shooter.FinalizeExtrusion(col, manipulatorLeg.currentFootPos);
                shooter.SetTetherAnchor(null);
                isManipulating = false;

                manipulatorLeg.StepTo(manipulatorLeg.currentFootPos, currentSurfaceNormal, col);
                manipulatorLeg = null;
            }

            if (distToHead > legExtensionMax * 1.5f)
            {
                if (shooter != null) shooter.SetTetherAnchor(null);
                isManipulating = false;
                manipulatorLeg.isPlanted = true;
                manipulatorLeg = null;
            }
        }
    }

    // ───────────────────────────────────────────────────────────
    //  Debug Gizmos
    // ───────────────────────────────────────────────────────────

    void OnDrawGizmos()
    {
        if (!showDebug || legs == null) return;

        Transform t = headT != null ? headT : transform;
        Vector2 headPos = headRb != null ? headRb.position : (Vector2)t.position;

        for (int i = 0; i < legs.Length; i++)
        {
            var leg = legs[i];
            if (leg == null) continue;

            Gizmos.color = leg.isPlanted ? Color.cyan : Color.magenta;
            Gizmos.DrawLine(headPos, leg.currentFootPos);
            Gizmos.DrawWireSphere(leg.currentFootPos, 0.2f);

            // Show minStride exclusion zone if moving
            if (leg.isPlanted && inputDirection.sqrMagnitude > 0.01f)
            {
                Gizmos.color = new Color(1, 0.5f, 0, 0.2f);
                Gizmos.DrawWireSphere(leg.currentFootPos, minStride);
            }

            if (leg.isStepping)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawLine(leg.currentFootPos, leg.targetFootPos);
                Gizmos.DrawSphere(leg.targetFootPos, 0.3f);
            }

            if (restingFootOffsets != null && i < restingFootOffsets.Length)
            {
                Vector2 rest = t.TransformPoint(restingFootOffsets[i]);
                Gizmos.color = new Color(1, 1, 1, 0.3f);
                Gizmos.DrawWireSphere(rest, 0.3f);

                if (smoothedInput.sqrMagnitude > 0.01f)
                {
                    float maxReach = leg.thighLength + leg.shinLength;
                    Vector2 ideal = (Vector2)t.TransformPoint(restingFootOffsets[i])
                                  + smoothedInput * maxReach * 0.4f;
                    Gizmos.color = Color.yellow;
                    Gizmos.DrawWireSphere(ideal, 0.2f);
                }
            }

            if (Application.isPlaying && leg.isPlanted)
            {
                float urg = Urgency(leg);
                Gizmos.color = urg > 1f ? Color.red : new Color(0.5f, 0.5f, 0.5f, 0.5f);
                float sz = Mathf.Clamp(urg * 0.15f, 0.05f, 0.5f);
                Gizmos.DrawSphere((Vector3)leg.currentFootPos + Vector3.up * 0.5f, sz);
            }
        }

        Gizmos.color = new Color(1, 0, 0, 0.4f);
        foreach (var ap in leftPoints) Gizmos.DrawSphere(ap.point, 0.1f);
        Gizmos.color = new Color(0, 0, 1, 0.4f);
        foreach (var ap in rightPoints) Gizmos.DrawSphere(ap.point, 0.1f);

        // Show Knee Ray Coverage (Faint rays to verify detection on corners)
        if (Application.isPlaying)
        {
            Gizmos.color = new Color(0, 1, 1, 0.05f);
            foreach (var leg in legs)
            {
                Vector2 knee = leg.GetKneePosition();
                float kneeStep = 360f / kneeRayCount;
                for (int i = 0; i < kneeRayCount; i++)
                {
                    float rad = i * kneeStep * Mathf.Deg2Rad;
                    Vector2 dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
                    Gizmos.DrawRay(knee, dir * (sightDistance * kneeRayScale));
                }
            }
        }

        Gizmos.color = Color.yellow;
        Gizmos.DrawRay(headPos, inputDirection * 3f);

        Gizmos.color = new Color(0, 1, 0, 0.08f);
        Gizmos.DrawWireSphere(headPos, legExtensionMax);
        Gizmos.DrawWireSphere(headPos, legExtensionMin);
        Gizmos.color = new Color(1, 0, 0, 0.1f);
        Gizmos.DrawWireSphere(headPos, headSafeArea);
    }
}