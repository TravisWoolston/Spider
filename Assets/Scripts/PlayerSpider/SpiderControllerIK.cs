using UnityEngine;
using System.Collections;
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
    public float dynamicBodyLeadRate = 5f;
    public float dynamicBodyLeadCap = 5f;
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
    public int rayCount = 100;
    public int kneeRayCount = 40;
    public float sightDistance = 15f;
    public float kneeRayScale = 0.6f;
    public float dirtDetectionRadius = 1.5f;

    [Header("Web Physics")]
    public float spiderWeightMult = 1.0f;
    public float webWeightScale => spiderWeightMult;

    [Header("Debug")]
    public bool showDebug = true;

    [Header("Digging")]
    public bool isDigging = false;
    public int maxDiggingLegs = 2;
    public float digRadius = 1.5f;
    public float digStrikeSpeed = 60f;
    public float digWindUpDist = 3f;
    public float digWindUpTime = 0.15f;
    public float digRecoveryTime = 0.12f;
    public float digStrikeInterval = 0.2f;

    [Header("Sparring")]
    public KeyCode strikeKey = KeyCode.Mouse0;
    public KeyCode sparStanceKey = KeyCode.E;
    public float sparStrikeSpeed = 180f;
    [Tooltip("Layers the spar strike can hit (include enemy head/body AND Web layers).")]
    public LayerMask strikeLayers;

    // ── Runtime state ──────────────────────────────────────────
    private float targetBodyHeight = 0f;
    [HideInInspector] public Vector2 inputDirection;
    [HideInInspector] public float aiHeightInput = 0f;
    private Vector2 smoothedInput;
    private Vector2 currentSurfaceNormal = Vector2.up;
    private float groundFactor = 1.0f;
    private int dirtLayer;
    private int digNextLegIndex = 0;
    private float digCooldownTimer = 0f;
    private float currentLeadOffset = 0f;

    public List<AttachmentPoint> leftPoints = new();
    public List<AttachmentPoint> rightPoints = new();
    private Vector2[] restingFootOffsets;
    private int nextLegIndex;
    private int lastSteppedIndex = -1;

    // Zero-allocation buffers for physics queries
    private RaycastHit2D[] hitBuffer = new RaycastHit2D[100];
    private Collider2D[] overlapBuffer = new Collider2D[2000]; // Can comfortably handle large webs

    // Cached head transform
    private Transform headT;
    private SpiderWebShooter shooter;
    private SpiderLegIK manipulatorLeg;
    private bool isManipulating = false;

    // Sparring stance state
    private bool isSparStance = false;
    private SpiderLegIK sparStanceLeft;   // Left-side leg in stance
    private SpiderLegIK sparStanceRight;  // Right-side leg in stance
    private SpiderLegIK sparTrackingLeg;  // Whichever stance leg is actively tracking the mouse
    private SpiderLegIK sparLeg;
    private bool isSparWindUp = false;

    // ───────────────────────────────────────────────────────────
    //  Lifecycle
    // ───────────────────────────────────────────────────────────

    void Start()
    {
        shooter = GetComponent<SpiderWebShooter>();
        dirtLayer = LayerMask.NameToLayer("Dirt");
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

                GlobalAttachmentManager.StoreAttachmentPoint(hit.point, hit.normal, hit.collider.gameObject.layer == LayerMask.NameToLayer("Web") ? hit.collider.transform : null);
            }
        }
    }

    void Update()
    {
        ReadInput();
        PerformRaycasts();

        if (inputDirection.sqrMagnitude > 0.01f)
            smoothedInput = Vector2.MoveTowards(smoothedInput, inputDirection, Time.deltaTime * 5f);
        else
            smoothedInput = Vector2.MoveTowards(smoothedInput, Vector2.zero, Time.deltaTime * 8f);

        foreach (var leg in legs)
            if (leg.isPlanted) leg.plantedTimer += Time.deltaTime;

        if (isDigging) HandleDigging();
        HandleSparring();
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
            if (Input.GetKey(KeyCode.LeftShift)) heightInput += 1f;
            if (Input.GetKey(KeyCode.Q) || Input.GetKey(KeyCode.LeftControl)) heightInput -= 1f;
            if (Input.GetKeyDown(KeyCode.R)) isDigging = !isDigging;

            targetBodyHeight += heightInput * bodyHeightSpeed * Time.deltaTime;
        }
        else
        {
            targetBodyHeight += aiHeightInput * bodyHeightSpeed * Time.deltaTime;
        }

        if (smoothedInput.sqrMagnitude > 0.1f)
        {
            float dodgeBias = Vector2.Dot(smoothedInput, currentSurfaceNormal);

            int pointsAhead = 0;
            foreach (var ap in leftPoints)
                if (Vector2.Dot(ap.point - (Vector2)headT.position, smoothedInput.normalized) > 0.5f) pointsAhead++;
            foreach (var ap in rightPoints)
                if (Vector2.Dot(ap.point - (Vector2)headT.position, smoothedInput.normalized) > 0.5f) pointsAhead++;

            int steppingLegs = 0;
            foreach (var leg in legs)
                if (leg.isStepping) steppingLegs++;

            if (steppingLegs == 0)
                currentLeadOffset = Mathf.MoveTowards(currentLeadOffset, dynamicBodyLeadCap, dynamicBodyLeadRate * Time.deltaTime);
            else
                currentLeadOffset = Mathf.MoveTowards(currentLeadOffset, 0f, dynamicBodyLeadRate * 2f * Time.deltaTime);

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

        // --- Dirt Avoidance ---
        Vector2 origin = headRb != null ? headRb.position : (Vector2)headT.position;
        Collider2D closestDirt = null;
        float minDirtDist = float.MaxValue;
        Vector2 closestPoint = origin;
        
        int overlapCount = Physics2D.OverlapCircleNonAlloc(origin, dirtDetectionRadius, overlapBuffer, 1 << dirtLayer);
        for (int i = 0; i < overlapCount; i++)
        {
            Collider2D col = overlapBuffer[i];
            Vector2 cp = col.ClosestPoint(origin);
            float dist = Vector2.Distance(origin, cp);
            if (dist < minDirtDist)
            {
                minDirtDist = dist;
                closestDirt = col;
                closestPoint = cp;
            }
        }
        
        if (closestDirt != null && minDirtDist < dirtDetectionRadius && minDirtDist > 0.001f)
        {
            Vector2 toHead = (origin - closestPoint).normalized;
            float dot = Vector2.Dot(toHead, currentSurfaceNormal);
            float push = (dirtDetectionRadius - minDirtDist);
            targetBodyHeight += dot * push * Time.deltaTime * 20f;
        }

        // Digging no longer overrides body height — maintain normal posture

        targetBodyHeight = Mathf.Clamp(targetBodyHeight, clampMin, bodyHeightMax);
        bodyHeight = Mathf.MoveTowards(bodyHeight, targetBodyHeight, bodyHeightSpeed * Time.deltaTime);
    }

    // ───────────────────────────────────────────────────────────
    //  Raycasting
    // ───────────────────────────────────────────────────────────

    void PerformRaycasts()
    {
        leftPoints.Clear();
        rightPoints.Clear();

        Vector2 origin = headRb != null ? headRb.position : (Vector2)headT.position;

        int webLayer = LayerMask.NameToLayer("Web");
        float step = 360f / rayCount;
        for (int i = 0; i < rayCount; i++)
        {
            float rad = i * step * Mathf.Deg2Rad;
            Vector2 dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));

            // Use RaycastAll so we can "See through" multiple strands of web
            int hitCount = Physics2D.RaycastNonAlloc(origin, dir, hitBuffer, sightDistance, attachableLayers);
            
            // RAYCASTALL IS NOT ORDERED: We must sort by distance to handle obstructions correctly
            if (hitCount > 0)
            {
                // Manual insertion sort for zero allocations (hitCount is typically small)
                for (int n = 1; n < hitCount; n++)
                {
                    var key = hitBuffer[n];
                    int m = n - 1;
                    while (m >= 0 && hitBuffer[m].distance > key.distance)
                    {
                        hitBuffer[m + 1] = hitBuffer[m];
                        m = m - 1;
                    }
                    hitBuffer[m + 1] = key;
                }

                for (int j = 0; j < hitCount; j++)
                {
                    var hit = hitBuffer[j];
                    if (hit.collider == null) continue;

                    float dist = Vector2.Distance(hit.point, origin);
                    if (hit.collider.gameObject.layer == webLayer && dist < 1.0f)
                        continue;

                    if (hit.collider.gameObject.layer == dirtLayer && (hit.distance == 0f || Vector2.Dot(dir, hit.normal) > 0f))
                        continue;

                    float localX = headT.InverseTransformPoint(hit.point).x;
                    var ap = new AttachmentPoint(hit.point, hit.normal, hit.collider);

                    if (localX < 0) leftPoints.Add(ap);
                    else            rightPoints.Add(ap);
                    
                    // Stop evaluating this ray ONLY if we hit a solid surface.
                    // If it's a web, keep looking for more web or ground behind it.
                    if (hit.collider.gameObject.layer != webLayer)
                        break;
                }
            }
        }

        // --- Knee-based Raycasting for local obstacle/cliff detection ---
        foreach (var leg in legs)
        {
            Vector2 kneeOrigin = leg.GetKneePosition();
            float kneeStep = 360f / kneeRayCount;
            for (int i = 0; i < kneeRayCount; i++)
            {
                float rad = i * kneeStep * Mathf.Deg2Rad;
                Vector2 dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
                int hitCount = Physics2D.RaycastNonAlloc(kneeOrigin, dir, hitBuffer, sightDistance * kneeRayScale, attachableLayers);
                
                if (hitCount > 0)
                {
                    // Manual insertion sort for zero allocations
                    for (int n = 1; n < hitCount; n++)
                    {
                        var key = hitBuffer[n];
                        int m = n - 1;
                        while (m >= 0 && hitBuffer[m].distance > key.distance)
                        {
                            hitBuffer[m + 1] = hitBuffer[m];
                            m = m - 1;
                        }
                        hitBuffer[m + 1] = key;
                    }

                    for (int j = 0; j < hitCount; j++)
                    {
                        var hit = hitBuffer[j];
                        if (hit.collider == null) continue;

                        float distFromHead = Vector2.Distance(hit.point, origin);
                        
                        // Allow webs even if they are very close to the head (overlap prevention)
                        bool isWeb = hit.collider.gameObject.layer == webLayer;
                        if (!isWeb && distFromHead < 1.0f)
                            continue;

                        if (hit.collider.gameObject.layer == dirtLayer && (hit.distance == 0f || Vector2.Dot(dir, hit.normal) > 0f))
                            continue;

                        float localX = headT.InverseTransformPoint(hit.point).x;
                        var ap = new AttachmentPoint(hit.point, hit.normal, hit.collider);
                        if (localX < 0) leftPoints.Add(ap);
                        else            rightPoints.Add(ap);
                    }
                }
            }
        }

        // --- Volume-based Web Detection (The "Safety Net") ---
        // This finds webs that radial rays might miss because they are too thin or aligned with the ray
        int overlapCount = Physics2D.OverlapCircleNonAlloc(origin, sightDistance, overlapBuffer, LayerMask.GetMask("Web"));
        for (int i = 0; i < overlapCount; i++)
        {
            Collider2D col = overlapBuffer[i];
            
            // To drastically improve performance, use the segment's closest point 
            // instead of generating multiple virtual sample points along the array.
            // With hundreds of segments, generating 8 virtual points per segment was causing O(n^2) frame drops.
            Vector2 pL = col.ClosestPoint(origin);
            float localX = headT.InverseTransformPoint(pL).x;
            var ap = new AttachmentPoint(pL, (origin - pL).normalized, col);
            if (localX < 0) leftPoints.Add(ap);
            else            rightPoints.Add(ap);
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
            // Respect the defaultBodyHeight set in the editor instead of overwriting it
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
            if (!leg.isPlanted || leg.isStepping || leg.isDigStriking) continue;

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
            if (l.isStepping || l.isDigStriking || l.isSparring || !l.isPlanted)
            {
                if (l.isPhysicallyRight) detachedR++;
                else detachedL++;
            }
        }

        SpiderLegIK best = null;
        float maxUrg = 1f;

        foreach (var leg in legs)
        {
            if (!leg.isPlanted || leg.isStepping || leg.isDigStriking || leg.isSparring) continue;

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

    public struct AttachmentPoint
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

        int webLayer = LayerMask.NameToLayer("Web");

        foreach (var ap in pool)
        {
            float distFromHead = Vector2.Distance(headPos, ap.point);
            bool isWeb = ap.collider != null && ap.collider.gameObject.layer == webLayer;

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
            
            // Strongly prefer webs since they are player-constructed fast travel / bridges
            if (isWeb) score += 40f;

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

        int webLayer = LayerMask.NameToLayer("Web");

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
    //  Sparring Routine
    // ───────────────────────────────────────────────────────────

    void HandleSparring()
    {
        if (isAI) return;

        // ── Toggle Stance with E ──
        if (Input.GetKeyDown(sparStanceKey))
        {
            if (!isSparStance)
                EnterSparStance();
            else
                ExitSparStance();
        }

        if (!isSparStance) return;

        // ── Determine which stance leg should track the mouse ──
        Vector2 mousePos = GetMouseWorldPos();
        float targetLocalX = headT.InverseTransformPoint(mousePos).x;
        bool mouseIsRight = targetLocalX >= 0;

        // The leg on the same side as the mouse tracks it
        SpiderLegIK desiredTracker = mouseIsRight ? sparStanceRight : sparStanceLeft;
        if (desiredTracker != null && desiredTracker != sparTrackingLeg)
        {
            // Swap tracking: old tracker holds its current arched position
            sparTrackingLeg = desiredTracker;
        }

        // ── Update both stance legs ──
        if (sparStanceLeft != null && !sparStanceLeft.isStepping && !sparStanceLeft.isSparStriking)
            UpdateStanceLeg(sparStanceLeft, sparStanceLeft == sparTrackingLeg ? mousePos : (Vector2?)null);
        if (sparStanceRight != null && !sparStanceRight.isStepping && !sparStanceRight.isSparStriking)
            UpdateStanceLeg(sparStanceRight, sparStanceRight == sparTrackingLeg ? mousePos : (Vector2?)null);

        // ── Click to Strike ──
        if (Input.GetKeyDown(strikeKey) && sparTrackingLeg != null && !sparTrackingLeg.isStepping && !sparTrackingLeg.isSparStriking)
        {
            int idx = System.Array.IndexOf(legs, sparTrackingLeg);
            Vector2 shoulderWorld = headT.TransformPoint(restingFootOffsets[idx]);
            sparTrackingLeg.LaunchSparStrike(mousePos, sparStrikeSpeed, shoulderWorld, sparTrackingLeg.thighLength + sparTrackingLeg.shinLength);

            // Immediately swap tracking to the other side
            sparTrackingLeg = (sparTrackingLeg == sparStanceLeft) ? sparStanceRight : sparStanceLeft;
        }
    }

    void EnterSparStance()
    {
        isSparStance = true;
        sparStanceLeft = PickBestStanceLeg(false);
        sparStanceRight = PickBestStanceLeg(true);

        if (sparStanceLeft != null) LiftLegIntoStance(sparStanceLeft);
        if (sparStanceRight != null) LiftLegIntoStance(sparStanceRight);

        // Default: track on the side closest to the mouse
        Vector2 mousePos = GetMouseWorldPos();
        float localX = headT.InverseTransformPoint(mousePos).x;
        sparTrackingLeg = localX >= 0 ? sparStanceRight : sparStanceLeft;
        if (sparTrackingLeg == null)
            sparTrackingLeg = sparStanceLeft ?? sparStanceRight;
    }

    void ExitSparStance()
    {
        isSparStance = false;

        // Drop any held objects before replanting
        if (sparStanceLeft != null && sparStanceLeft.isHolding)
            sparStanceLeft.DropHeldObject();
        if (sparStanceRight != null && sparStanceRight.isHolding)
            sparStanceRight.DropHeldObject();

        if (sparStanceLeft != null && sparStanceLeft.isSparring && !sparStanceLeft.isStepping)
            ReplantStanceLeg(sparStanceLeft);
        if (sparStanceRight != null && sparStanceRight.isSparring && !sparStanceRight.isStepping)
            ReplantStanceLeg(sparStanceRight);

        sparStanceLeft = null;
        sparStanceRight = null;
        sparTrackingLeg = null;
    }

    SpiderLegIK PickBestStanceLeg(bool rightSide)
    {
        SpiderLegIK best = null;
        float bestDist = float.MaxValue;
        Vector2 headPos = headRb != null ? headRb.position : (Vector2)headT.position;

        for (int i = 0; i < legs.Length; i++)
        {
            var leg = legs[i];
            if (leg.isPhysicallyRight != rightSide) continue;
            if (leg.isStepping || leg.isDigStriking || leg.isSparring) continue;
            if (!leg.isPlanted) continue;

            float d = Vector2.Distance(leg.currentFootPos, headPos);
            if (d < bestDist)
            {
                bestDist = d;
                best = leg;
            }
        }
        return best;
    }

    void LiftLegIntoStance(SpiderLegIK leg)
    {
        leg.isSparring = true;
        leg.isPlanted = false;
        leg.plantedTransform = null;
    }

    void ReplantStanceLeg(SpiderLegIK leg)
    {
        // Find a nearby surface to plant the foot
        int idx = System.Array.IndexOf(legs, leg);
        Vector2 shoulderWorld = headT.TransformPoint(restingFootOffsets[idx]);
        float reach = leg.thighLength + leg.shinLength;

        RaycastHit2D hit = Physics2D.Raycast(shoulderWorld, -headT.up, reach, attachableLayers);
        if (hit.collider != null)
        {
            leg.sparAimTarget = Vector2.zero;
            leg.isSparring = false;
            leg.StepTo(hit.point, hit.normal, hit.collider);
        }
        else
        {
            // Fallback: just drop the sparring flag so the gait can reclaim it
            leg.sparAimTarget = Vector2.zero;
            leg.isSparring = false;
            leg.isPlanted = false;
        }
    }

    void UpdateStanceLeg(SpiderLegIK leg, Vector2? trackTarget)
    {
        int idx = System.Array.IndexOf(legs, leg);
        if (idx < 0 || restingFootOffsets == null) return;

        Vector2 shoulderWorld = headT.TransformPoint(restingFootOffsets[idx]);

        // If holding a dead spider, keep the leg in a carry arch —
        // SyncToSurface will move the held object to follow the foot.
        if (leg.isHolding)
        {
            Vector2 upDir = (Vector2)headT.up;
            float sideSign = leg.isPhysicallyRight ? 1f : -1f;
            Vector2 carryTarget = shoulderWorld + upDir * (leg.thighLength * 0.4f)
                                + (Vector2)headT.right * sideSign * (leg.thighLength * 0.3f);
            leg.currentFootPos = Vector2.Lerp(leg.currentFootPos, carryTarget, Time.deltaTime * 12f);
            leg.sparAimTarget = Vector2.zero;
            leg.plantedNormal = upDir;
            return;
        }

        if (trackTarget.HasValue)
        {
            // ── TRACKING LEG: shin aims at mouse, foot cocked back ──
            Vector2 mousePos = trackTarget.Value;
            Vector2 dirToMouse = (mousePos - shoulderWorld).normalized;

            // Foot sits behind shoulder along aim line — cocked spear position
            Vector2 windUpPos = shoulderWorld - dirToMouse * (leg.thighLength * 0.3f);
            leg.currentFootPos = Vector2.Lerp(leg.currentFootPos, windUpPos, Time.deltaTime * 40f);
            leg.sparAimTarget = mousePos;

            // Knee bends away from the aim line
            Vector2 aimPerp = new Vector2(-dirToMouse.y, dirToMouse.x);
            leg.plantedNormal = aimPerp * (leg.isPhysicallyRight ? -1f : 1f);
        }
        else
        {
            // ── IDLE STANCE LEG: hold an arched "guard" position above the shoulder ──
            Vector2 upDir = (Vector2)headT.up;
            float sideSign = leg.isPhysicallyRight ? 1f : -1f;
            Vector2 archTarget = shoulderWorld + upDir * (leg.thighLength * 0.5f)
                               + (Vector2)headT.right * sideSign * (leg.thighLength * 0.3f);
            leg.currentFootPos = Vector2.Lerp(leg.currentFootPos, archTarget, Time.deltaTime * 20f);
            leg.sparAimTarget = Vector2.zero;
            leg.plantedNormal = upDir;
        }
    }

    // ───────────────────────────────────────────────────────────
    //  Digging Routine  (Queued Punch System)
    // ───────────────────────────────────────────────────────────

    void HandleDigging()
    {
        digCooldownTimer -= Time.deltaTime;
        if (digCooldownTimer > 0f) return;

        Vector2 mousePos = GetMouseWorldPos();
        Vector2 headPos = headRb != null ? headRb.position : (Vector2)headT.position;

        // Don't dig if the mouse is too far from the spider
        float mouseDistFromHead = Vector2.Distance(mousePos, headPos);
        if (mouseDistFromHead > legExtensionMax * 1.2f) return;

        // Cast a line-of-sight ray from the spider head toward the mouse. This naturally 
        // finds the nearest valid surface on the terrain, preventing poking holes inside 
        // the chunk and completely overriding the older vertical drop-ray.
        Vector2 rayDir = (mousePos - headPos).normalized;
        int hitCount = Physics2D.RaycastNonAlloc(headPos, rayDir, hitBuffer, legExtensionMax * 1.2f, attachableLayers);
        
        RaycastHit2D surfaceHit = default;
        float minDist = float.MaxValue;
        int webLayer = LayerMask.NameToLayer("Web");

        for (int i = 0; i < hitCount; i++)
        {
            var hit = hitBuffer[i];
            if (hit.collider == null || hit.collider.gameObject.layer == webLayer) continue;

            // Reject internal zero-distance contacts or backfaces
            if (hit.collider.gameObject.layer == dirtLayer && (hit.distance == 0f || Vector2.Dot(rayDir, hit.normal) > 0f))
                continue;

            if (hit.distance < minDist)
            {
                minDist = hit.distance;
                surfaceHit = hit;
            }
        }

        if (surfaceHit.collider == null) return;
        
        Vector2 surfacePoint = surfaceHit.point;
        Vector2 surfaceNormal = surfaceHit.normal;

        // Determine which side of the spider the dig target is on
        float targetLocalX = headT.InverseTransformPoint(surfacePoint).x;
        bool targetIsRight = targetLocalX >= 0;

        // Count how many legs are currently digging — bail if at max
        int currentlyDigging = 0;
        foreach (var leg in legs)
            if (leg.isDigStriking) currentlyDigging++;
        if (currentlyDigging >= maxDiggingLegs) return;

        // Find the best available leg using round-robin starting from digNextLegIndex
        SpiderLegIK chosenLeg = null;
        float bestScore = float.MaxValue;

        for (int i = 0; i < legs.Length; i++)
        {
            // Round-robin: start from digNextLegIndex and wrap around
            int idx = (digNextLegIndex + i) % legs.Length;
            var leg = legs[idx];

            // Skip legs that are busy
            if (leg.isStepping || leg.isDigStriking || leg.isSparring) continue;
            if (!leg.isPlanted) continue;

            // Only same-side legs can dig — prevents cross-body reaching
            if (leg.isPhysicallyRight != targetIsRight) continue;

            // Check the leg can reach the target
            float reach = leg.thighLength + leg.shinLength;
            Vector2 shoulderPos = (Vector2)headT.TransformPoint(restingFootOffsets[idx]);
            float distToTarget = Vector2.Distance(surfacePoint, shoulderPos);
            if (distToTarget > reach * 0.95f) continue;

            // Score by distance from foot to target (closer is better)
            float d = Vector2.Distance(leg.currentFootPos, surfacePoint);
            if (d < bestScore)
            {
                bestScore = d;
                chosenLeg = leg;
                digNextLegIndex = (idx + 1) % legs.Length;
            }
        }

        if (chosenLeg != null)
        {
            chosenLeg.DigStrike(surfacePoint, surfaceNormal, surfaceHit.collider, digWindUpDist, digStrikeSpeed, digRadius, digWindUpTime, digRecoveryTime);
            digCooldownTimer = digStrikeInterval;
        }
    }

    private Vector2 GetMouseWorldPos()
    {
        if (Camera.main.orthographic)
            return Camera.main.ScreenToWorldPoint(Input.mousePosition);

        // Perspective Camera Fix: Use a ray through the mouse and 
        // find its intersection with the Z=0 plane (terrain level)
        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
        Plane worldPlane = new Plane(Vector3.forward, Vector3.zero); // Face the camera at Z=0

        if (worldPlane.Raycast(ray, out float entry))
        {
            return ray.GetPoint(entry);
        }
        return Camera.main.ScreenToWorldPoint(new Vector3(Input.mousePosition.x, Input.mousePosition.y, 10f));
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
            // Skip digging legs from body pose — they are mid-animation and would
            // drag the body downward/underground
            if (leg.isDigStriking || leg.isSparring) continue;
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

        int webLayer = LayerMask.NameToLayer("Web");
        int groundPlanted = 0;
        foreach (var leg in legs)
        {
            if (!leg.isPlanted || leg.plantedTransform == null) continue;
            int legLayer = leg.plantedTransform.gameObject.layer;
            if (legLayer != webLayer && legLayer != dirtLayer)
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

        // Measure how coherent the planted normals are (1 = all agree, ~0 = opposing).
        float normalCoherence = normalSum.magnitude / Mathf.Max(1, planted);

        Vector2 rawAvgNormal;
        if (normalSum.sqrMagnitude < 0.01f)
            rawAvgNormal = (Vector2)headT.up;
        else
            rawAvgNormal = normalSum.normalized;

        // When normals are incoherent (tight spaces with floor + ceiling feet),
        // smooth the surface normal very slowly so it doesn't flip toward the ceiling.
        float normalSmoothRate = normalCoherence > 0.5f ? 8f : 1.5f;
        currentSurfaceNormal = Vector2.Lerp(currentSurfaceNormal, rawAvgNormal,
            Time.fixedDeltaTime * normalSmoothRate).normalized;
        Vector2 avgNormal = currentSurfaceNormal;

        float num = 0, den = 0;
        Vector2 center = (avgFootPos / planted);
        for (int i = 0; i < legs.Length; i++)
        {
            if (legs[i].isDigStriking || legs[i].isSparring) continue;
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

            // Hysteresis: strongly prefer the current orientation to prevent rapid flipping.
            // The alternative must be significantly closer (by this margin) to win.
            float hysteresis = 30f;

            if (groundFactor > 0.3f)
            {
                float normAngle = Mathf.Atan2(avgNormal.y, avgNormal.x) * Mathf.Rad2Deg - 90f;
                float normDiff1 = Mathf.Abs(Mathf.DeltaAngle(normAngle, targetAngle));
                float normDiff2 = Mathf.Abs(Mathf.DeltaAngle(normAngle, altAngle));

                if (normalCoherence > 0.4f)
                {
                    // Normal is reliable: use it, but still apply hysteresis
                    // favouring whichever candidate is closer to current rotation.
                    if (normDiff2 < normDiff1)
                    {
                        // Alt is better per normal; only switch if current rotation
                        // isn't already much closer to the original candidate.
                        if (diff1 > diff2 + hysteresis)
                            targetAngle = altAngle;
                        else if (diff2 < diff1)
                            targetAngle = altAngle;
                    }
                    // else: targetAngle is already the better normal-aligned candidate
                }
                else
                {
                    // Normal is unreliable (tight space): fall back to current rotation
                    if (diff2 + hysteresis < diff1)
                        targetAngle = altAngle;
                }
            }
            else
            {
                // No strong ground contact: prefer current rotation with hysteresis
                if (diff2 + hysteresis < diff1)
                    targetAngle = altAngle;
            }
        }

        Quaternion stableRot = Quaternion.Euler(0, 0, targetAngle);

        Vector2 posSum = Vector2.zero;
        for (int i = 0; i < legs.Length; i++)
        {
            if (legs[i].isDigStriking || legs[i].isSparring) continue;
            if (!legs[i].isPlanted) continue;
            Vector2 worldOffset = stableRot * (Vector3)restingFootOffsets[i];
            posSum += (legs[i].currentFootPos - worldOffset);
        }

        Vector2 targetPos = (posSum / planted);

        // Surface-push raycast: prevent body from clipping into terrain.
        // When normals are incoherent (tight space), cast in multiple directions
        // and push away from the nearest surface to stay centered.
        float searchRadius = bodyHeight > 0 ? bodyHeight * 2.0f : 2.0f;
        LayerMask bodyCheckMask = attachableLayers;

        if (normalCoherence > 0.5f)
        {
            // Single-direction push along the reliable avgNormal
            RaycastHit2D centerHit = Physics2D.Raycast(targetPos + avgNormal * searchRadius, -avgNormal, searchRadius * 2.0f, bodyCheckMask);
            if (centerHit.collider != null)
            {
                float surfaceProg = Vector2.Dot(centerHit.point, avgNormal);
                float targetProg = Vector2.Dot(targetPos, avgNormal);
                if (targetProg < surfaceProg)
                {
                    targetPos += avgNormal * (surfaceProg - targetProg);
                }
            }
        }
        else
        {
            // Tight space: cast in 4 cardinal directions and push away from
            // the closest surface to keep the body centered in the tunnel.
            Vector2[] pushDirs = { Vector2.up, Vector2.down, Vector2.left, Vector2.right };
            float pushRadius = searchRadius > 1f ? searchRadius : 2f;
            float minDist = float.MaxValue;
            Vector2 pushNormal = Vector2.zero;

            for (int d = 0; d < 4; d++)
            {
                RaycastHit2D hit = Physics2D.Raycast(targetPos, pushDirs[d], pushRadius, bodyCheckMask);
                if (hit.collider != null && hit.distance < minDist)
                {
                    minDist = hit.distance;
                    pushNormal = hit.normal;
                }
            }

            if (minDist < pushRadius && pushNormal.sqrMagnitude > 0.01f)
            {
                float penetration = Mathf.Max(0f, 1f - minDist);
                targetPos += pushNormal * penetration;
            }
        }

        float confidence = Mathf.Max(groundFactor, strandFactor * 0.7f);

        // Scale down body height offset when normals are incoherent to prevent
        // the spider being pushed into the ceiling along a confused avgNormal.
        float coherenceScale = Mathf.Lerp(0.1f, 1.0f, Mathf.Clamp01(normalCoherence * 2f));
        float effectHeight = bodyHeight * Mathf.Lerp(0.2f, 1.0f, confidence) * coherenceScale;
        float effectLead = (bodyLead + currentLeadOffset) * Mathf.Lerp(0.4f, 1.0f, confidence);

        targetPos += avgNormal * effectHeight;

        if (smoothedInput.sqrMagnitude > 0.01f)
        {
            targetPos += (Vector2)smoothedInput * effectLead;
        }

        Vector2 toTarget = targetPos - headRb.position;

        // Count actively digging legs to dampen body movement during dig cycles.
        int diggingCount = 0;
        foreach (var leg in legs)
            if (leg.isDigStriking || leg.isSparring) diggingCount++;
        float digDampen = diggingCount > 0 ? Mathf.Lerp(1f, 0.4f, (float)diggingCount / Mathf.Max(1, legs.Length)) : 1f;

        float surfaceSmoothing = Mathf.Lerp(bodySmoothing * 0.7f, bodySmoothing, groundFactor);
        float currentSmoothing = (inputDirection.sqrMagnitude > 0.01f) ? surfaceSmoothing : surfaceSmoothing * 0.2f;
        currentSmoothing *= digDampen;

        Vector2 gravityCancel = -Physics2D.gravity * headRb.gravityScale;

        headRb.linearVelocity = Vector2.Lerp(headRb.linearVelocity, toTarget * currentSmoothing, Time.fixedDeltaTime * 10f);
        headRb.AddForce(gravityCancel * headRb.mass, ForceMode2D.Force);

        float effectiveRotSpeed = rotationSpeed * digDampen;
        headT.rotation = Quaternion.Slerp(headT.rotation, stableRot, Time.fixedDeltaTime * effectiveRotSpeed);
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
    //  Death
    // ───────────────────────────────────────────────────────────

    [HideInInspector] public bool isDead = false;

    /// <summary>
    /// Kills this spider: ragdolls the body, releases all legs, and
    /// disables IK/AI so it becomes a limp physics object.
    /// </summary>
    public void Die()
    {
        if (isDead) return;
        isDead = true;

        // Stop all leg coroutines (stepping, digging, sparring animations)
        foreach (var leg in legs)
        {
            leg.StopAllCoroutines();
            leg.isStepping = false;
            leg.isDigStriking = false;
            leg.isSparring = false;
            leg.isSparStriking = false;
            leg.isPlanted = false;
            leg.plantedTransform = null;
            leg.sparAimTarget = Vector2.zero;
            leg.enabled = false;

            // Re-enable physics on each segment so they ragdoll
            EnableRagdollPhysics(leg.thigh);
            EnableRagdollPhysics(leg.shin);
            // Foot: enable rigidbody only — its only hinge is the hold joint,
            // not a structural chain joint, so we must not re-enable it.
            EnableRagdollBody(leg.foot);
        }

        // Ragdoll the head: let gravity take over, unfreeze rotation
        if (headRb != null)
        {
            headRb.freezeRotation = false;
            headRb.gravityScale = 1f;
            headRb.linearVelocity = Vector2.zero;
            headRb.angularVelocity = Random.Range(-60f, 60f);
        }

        // Disable AI if present
        var ai = GetComponent<SpiderAI>();
        if (ai != null) ai.enabled = false;

        // Disable this controller so Update/FixedUpdate stop
        enabled = false;
    }

    /// <summary>
    /// Reverses DisablePhysics: sets the Rigidbody2D back to Dynamic with gravity
    /// and re-enables all HingeJoint2D components so limbs stay connected.
    /// </summary>
    void EnableRagdollPhysics(Transform t)
    {
        if (t == null) return;
        var rb = t.GetComponent<Rigidbody2D>();
        if (rb != null)
        {
            // CRITICAL: Sync the rigidbody to the current transform position BEFORE
            // switching to Dynamic. During IK, transforms were moved directly while the
            // Kinematic rigidbody's internal physics position stayed stale. Without this
            // sync, switching to Dynamic causes the body to snap to its old position and
            // the hinge joints explode trying to resolve the mismatch.
            rb.position = t.position;
            rb.rotation = t.rotation.eulerAngles.z;
            rb.bodyType = RigidbodyType2D.Dynamic;
            rb.gravityScale = 1f;
            rb.linearVelocity = Vector2.zero;
            rb.angularVelocity = 0f;
        }
        foreach (var j in t.GetComponents<HingeJoint2D>())
            j.enabled = true;
    }

    /// <summary>
    /// Enables the Rigidbody2D for ragdoll but does NOT enable any HingeJoint2Ds.
    /// Used for the foot, whose only hinge is the hold joint (not a structural limb joint).
    /// </summary>
    void EnableRagdollBody(Transform t)
    {
        if (t == null) return;
        var rb = t.GetComponent<Rigidbody2D>();
        if (rb != null)
        {
            rb.position = t.position;
            rb.rotation = t.rotation.eulerAngles.z;
            rb.bodyType = RigidbodyType2D.Dynamic;
            rb.gravityScale = 1f;
            rb.linearVelocity = Vector2.zero;
            rb.angularVelocity = 0f;
        }
    }

    // ───────────────────────────────────────────────────────────
    //  Helpers
    // ───────────────────────────────────────────────────────────

    int DetachedCount()
    {
        int n = 0;
        foreach (var leg in legs)
            if (leg.isStepping || leg.isDigStriking || leg.isSparring || !leg.isPlanted) n++;
        return n;
    }

    // ───────────────────────────────────────────────────────────
    //  Web Manipulation
    // ───────────────────────────────────────────────────────────

    void HandleWebManipulation()
    {
        if (shooter == null || !shooter.enabled) return;

        Vector2 mousePos = GetMouseWorldPos();
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

            int webLayer = LayerMask.NameToLayer("Web");
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
        Gizmos.color = new Color(0.6f, 0.3f, 0f, 0.2f);
        Gizmos.DrawWireSphere(headPos, dirtDetectionRadius);
    }
}