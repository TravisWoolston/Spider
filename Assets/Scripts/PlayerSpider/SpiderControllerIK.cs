using UnityEngine;
using System.Collections.Generic;

public class SpiderControllerIK : MonoBehaviour
{
    // ── Enums ──────────────────────────────────────────────────
    public enum GaitType   { Urgency, Alternate }
    public enum SolverMode { Analytical, LimbSolver }

    // ── Inspector ──────────────────────────────────────────────
    [Header("IK Solver")]
    public SolverMode solverMode = SolverMode.LimbSolver;
    public bool isAI = false;

    [Header("Legs")]
    public SpiderLegIK[] legs;
    public GaitType activeGait = GaitType.Urgency;
    public bool   autoDetectSide = true;
    public int   maxDetached    = 2;
    public float legExtensionMax = 10f;
    public float legExtensionMin = 1.5f;
    public float footSpacing = 2f;
    public float minStride = 1f;
    public float headSafeArea  = 3f;

    [Header("Body")]
    public Rigidbody2D headRb;
    public float bodySmoothing  = 10f;
    public float bodyLead       = 2f;
    public float bodyHeight     = 0f;
    public float defaultBodyHeight = 0f;
    public float bodyHeightMin  = -10f;
    public float bodyHeightMax  = 10f;
    public float bodyHeightSpeed = 12f;
    public float rotationSpeed  = 5f;
    public float stepCooldown    = 0.3f;
    public float stepSpeed       = 20f;
    public float stepHeight      = 2f;
    public LayerMask attachableLayers;

    [Header("Raycasting")]
    public int   rayCount      = 100;
    public int   kneeRayCount  = 40;
    public float sightDistance  = 15f;
    public float kneeRayScale   = 0.6f;

    [Header("Web Physics")]
    public float spiderWeightMult = 1.0f; // Factor of spider's mass applied to web segments
    public float webWeightScale => spiderWeightMult; // Deprecated shim

    [Header("Debug")]
    public bool showDebug = true;

    // ── Runtime state ──────────────────────────────────────────
    private float targetBodyHeight = 0f;
    [HideInInspector] public Vector2 inputDirection;
    private Vector2 smoothedInput;
    private Vector2 currentSurfaceNormal = Vector2.up;
    private float groundFactor = 1.0f; // 1.0 on solid ground, 0.0 on webs

    private List<AttachmentPoint> leftPoints  = new();
    private List<AttachmentPoint> rightPoints = new();
    private Vector2[] restingFootOffsets;
    private int nextLegIndex;
    private int lastSteppedIndex = -1;

    // Zero-allocation buffers for physics queries
    private RaycastHit2D[] hitBuffer = new RaycastHit2D[100];
    private Collider2D[] overlapBuffer = new Collider2D[2000]; // Can comfortably handle large webs

    // Cached head transform — all orientation/position goes through this
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
        if (isAI)
        {
            // 1. Auto-discover Legs
            if (legs == null || legs.Length == 0)
                legs = GetComponentsInChildren<SpiderLegIK>();

            // 2. Auto-discover Head/Body
            if (headRb == null) headRb = GetComponent<Rigidbody2D>();
            if (headRb == null) headRb = GetComponentInChildren<Rigidbody2D>();
        }

        // Basic initialization required for both Player and AI
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
                // Knee direction usually mirrors physical side for spiders
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
                leg.targetFootPos  = hit.point;
                leg.plantedNormal  = hit.normal;
                leg.isPlanted = true;
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
        
        ProcessGait();
        HandleWebManipulation();

        // Reactive Mid-Step Adjustment: Update legs currently in flight
        foreach (var leg in legs)
        {
            if (leg.isStepping && leg.stepProgress < 0.6f) // Lock in target for the final 40% of the step
            {
                AttachmentPoint best = GetBestPoint(leg);
                if (best.isValid)
                {
                    // Only update if the new target is significantly different or better for the new direction
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
        // 1. Hardware Polling (Only for non-AI)
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
            if (Input.GetKeyDown(KeyCode.R)) targetBodyHeight = defaultBodyHeight;
            targetBodyHeight += heightInput * bodyHeightSpeed * Time.deltaTime;
        }

        // 2. Automated & Stick-Based dodging
        if (smoothedInput.sqrMagnitude > 0.1f)
        {
            // Use the average surface normal for orientation-agnostic dodging
            float dodgeBias = Vector2.Dot(smoothedInput, currentSurfaceNormal);
            
            // Auto Reach: If moving into empty space, hunker
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

        // Clamp height: Prevent negative height if on solid ground
        float minHeight = groundFactor > 0.1f ? 0.2f : bodyHeightMin;
        targetBodyHeight = Mathf.Clamp(targetBodyHeight, minHeight, bodyHeightMax);
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

                    float localX = headT.InverseTransformPoint(hit.point).x;
                    var ap = new AttachmentPoint(hit.point, hit.normal, hit.collider);

                    if (localX < 0) leftPoints.Add(ap);
                    else            rightPoints.Add(ap);
                    
                    // Store in global manager for AI wandering
                    GlobalAttachmentManager.StoreAttachmentPoint(hit.point, hit.normal, hit.collider.gameObject.layer == webLayer ? hit.collider.transform : null);
                    
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

                        float localX = headT.InverseTransformPoint(hit.point).x;
                        var ap = new AttachmentPoint(hit.point, hit.normal, hit.collider);
                        if (localX < 0) leftPoints.Add(ap);
                        else            rightPoints.Add(ap);
                        
                        // Store in global manager for AI wandering
                        GlobalAttachmentManager.StoreAttachmentPoint(hit.point, hit.normal, isWeb ? hit.collider.transform : null);
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
            restingFootOffsets[i] = new Vector2(local.x, 0); // Flatten Y to remove hardcoded height
        }
        
        // Move the design height from offsets into the dynamic bodyHeight variable
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
        if (activeGait == GaitType.Urgency)  UrgencyGait();
        else                                  AlternateGait();
    }

    void UrgencyGait()
    {
        // 1. Emergency Check: Over-extended legs MUST step regardless of gait constraints
        foreach (var leg in legs)
        {
            if (!leg.isPlanted || leg.isStepping) continue;
            if (leg.GetExtension() > legExtensionMax * 1.05f) // Small buffer
            {
                if (HasValidTarget(leg))
                {
                    StepLeg(leg);
                    lastSteppedIndex = System.Array.IndexOf(legs, leg);
                    return; // Handle one emergency per frame to maintain some order
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
                // ONLY consider a leg for stepping if it actually has somewhere better to go
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

        // Calculate the "Stable Base" (average of all planted feet)
        // We use this for IdealPos to decouple gait from head bobbing/dodging.
        Vector2 avgPlantedCenter = Vector2.zero;
        int plantedCount = 0;
        foreach (var l in legs) { if (l.isPlanted) { avgPlantedCenter += l.currentFootPos; plantedCount++; } }
        if (plantedCount > 0) avgPlantedCenter /= plantedCount;
        else avgPlantedCenter = (Vector2)headT.position;

        // Ideal position is relative to the center of the feet, not the current shifting head
        float maxReach = leg.thighLength + leg.shinLength;
        Vector2 idealPos = avgPlantedCenter + (Vector2)(headT.rotation * restingFootOffsets[idx])
                         + smoothedInput * maxReach * 0.4f;

        float distFromIdeal = Vector2.Distance(leg.currentFootPos, idealPos);
        float idealUrgency = distFromIdeal / (maxReach * 0.7f);

        float ext = leg.GetExtension() / legExtensionMax;
        float minExt = leg.GetExtension() / legExtensionMin;
        float extensionUrgency = (ext > 0.9f) ? (ext - 0.9f) * 10f : 0f;
        if (minExt < 1.1f) extensionUrgency += (1.1f - minExt) * 2f;

        // 4. Rhythm/Alternating Bias
        float rhythmUrgency = 0f;
        if (lastSteppedIndex != -1)
        {
            // Prefer legs on the opposite side
            bool sameSide = legs[lastSteppedIndex].isPhysicallyRight == leg.isPhysicallyRight;
            rhythmUrgency = sameSide ? 0f : 0.4f;
        }

        // Only have high positional urgency if we can actually take a significant stride
        float positionalUrgency = (distFromIdeal < minStride || smoothedInput.sqrMagnitude < 0.01f) ? 0f : idealUrgency;

        // 6. Proximity Urgency (Avoid legs bunching up)
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
        int webLayer = LayerMask.NameToLayer("Web");
        int idx = System.Array.IndexOf(legs, leg);
        List<AttachmentPoint> pool = leg.isPhysicallyRight ? rightPoints : leftPoints;
        if (pool.Count == 0) return new AttachmentPoint { isValid = false };

        // Stable Base for Ideal Pos
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
            
            // On webs, allow stepping much closer to the head to ensure we don't "miss" the strand segment under us
            float effectSafeArea = isWeb ? headSafeArea * 0.5f : headSafeArea;
            if (distFromHead < effectSafeArea) continue; 
            
            if (IsPointTooCloseToOtherFeet(ap.point, leg)) continue;

            // Significant stride check ONLY for the start of a step
            if (!leg.isStepping && inputDirection.sqrMagnitude > 0.01f)
            {
                if (Vector2.Distance(leg.currentFootPos, ap.point) < minStride * (isWeb ? 0.5f : 1.0f))
                    continue;
            }

            float localX = headT.InverseTransformPoint(ap.point).x;
            float score = 0f;

            // 1. Distance to predictive goal
            score -= Vector2.Distance(idealPos, ap.point) * 2.0f;
            
            // 2. Center-line avoidance (RELAXED ON WEBS)
            float sideBias = leg.isPhysicallyRight ? 1f : -1f;
            float sideMargin = isWeb ? -0.5f : 0.1f; // Allow reaching across the center-line on webs
            if (localX * sideBias < sideMargin) continue; 

            // 4. Directional Progressive Check (HARD FILTER)
            if (smoothedInput.sqrMagnitude > 0.05f)
            {
                Vector2 stepVec = (ap.point - leg.currentFootPos);
                float progress = Vector2.Dot(stepVec, smoothedInput.normalized);
                if (progress < -minStride * 0.1f) continue; 
            }

            // 3. Extension preference
            float extDiff = Mathf.Abs(distFromHead - comfortZone);
            score -= extDiff * 1.5f;

            // 5. Huge Step Penalty
            float moveDist = Vector2.Distance(leg.currentFootPos, ap.point);
            if (moveDist > maxReach * 1.2f) score -= 10f;

            // 4. Directional consistency
            if (smoothedInput.sqrMagnitude > 0.05f)
            {
                Vector2 shoulderPos = (Vector2)headT.TransformPoint(restingFootOffsets[idx]);
                Vector2 toPointRel = ap.point - shoulderPos;
                Vector2 inputNorm = smoothedInput.normalized;
                
                float projection = Vector2.Dot(toPointRel, inputNorm);
                score += projection * 5f; 
                score += Vector2.Dot(toPointRel.normalized, inputNorm) * 8f;
            }

            // 5. Directional/Orientation Scores
            // Removed localY penalty and normal-up bias to avoid 'upward distinction'
            score += Mathf.Abs(localX) * 2f;
            // score += Vector2.Dot(ap.normal, headT.up) * 3f; 

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

            // Longitudinal check (side detection)
            float localX = headT.InverseTransformPoint(ap.point).x;
            float sideBias = leg.isPhysicallyRight ? 1f : -1f;
            float sideMargin = isWeb ? -0.5f : 0.1f;
            if (localX * sideBias < sideMargin) continue;

            if (IsPointTooCloseToOtherFeet(ap.point, leg)) continue;

            // Must be a significant step
            float stepDist = Vector2.Distance(leg.currentFootPos, ap.point);
            if (stepDist < minStride * (isWeb ? 0.5f : 1.0f)) continue;

            // If moving, must provide forward progress relative to SMOOTHED input
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
    //  Body Pose Solver
    // ───────────────────────────────────────────────────────────

    void SolveBodyPose()
    {
        if (restingFootOffsets == null || legs == null) return;

        // 0. Synchronize all legs to their moving surfaces (webs) 
        foreach (var leg in legs) leg.SyncToSurface();

        int planted = 0;
        Vector2 avgFootPos = Vector2.zero;
        Vector2 normalSum  = Vector2.zero;

        // 1. Gather status from planted legs
        foreach (var leg in legs)
        {
            if (!leg.isPlanted) continue;
            
            avgFootPos   += leg.currentFootPos;
            normalSum    += leg.plantedNormal;
            
            planted++;
        }

        if (planted == 0)
        {
            headRb.freezeRotation = false; // Fall naturally
            return;
        }

        // Calculate Groundedness (How many legs are on solid surfaces vs webs)
        int webLayer = LayerMask.NameToLayer("Web");
        int groundPlanted = 0;
        foreach (var leg in legs)
        {
            if (leg.isPlanted && leg.plantedTransform != null && leg.plantedTransform.gameObject.layer != webLayer)
                groundPlanted++;
        }
        // groundFactor is 1.0 on solid floor, ~0.0 on webs.
        groundFactor = (float)groundPlanted / Mathf.Max(1, planted);

        // Multi-leg stability on strands: 
        // If we have many legs on the SAME strand, we can move more confidently.
        int maxOnSameStrand = 0;
        var counts = new System.Collections.Generic.Dictionary<Collider2D, int>();
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

        // 2. Determine "Stable Frame" (Shortest-path rotation for a vertically symmetrical spider)
        // Derived from foot locations to ensure the body "squares up" to its sprawl.
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
            
            // 180-degree symmetry management: pick shortest path 
            // but bias towards surface normal if on solid ground.
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

        // 3. Calculate target position using the stable frame to transform offsets.
        // This decouples the target calculation from the current head rotation, stopping wobbles.
        Vector2 posSum = Vector2.zero;
        for (int i = 0; i < legs.Length; i++)
        {
            if (!legs[i].isPlanted) continue;
            Vector2 worldOffset = stableRot * (Vector3)restingFootOffsets[i];
            posSum += (legs[i].currentFootPos - worldOffset);
        }
        
        Vector2 targetPos = (posSum / planted);

        // 3.5 Surface-Aware Height Adjustment: Prevent the body from 'clipping' into sharp crests.
        // On sharp curves, the average foot position (posSum/planted) is often deep inside the collider.
        // We do a belly-raycast to find the actual ground under our center and use that as the height base.
        float searchRadius = bodyHeight > 0 ? bodyHeight * 2.0f : 2.0f;
        RaycastHit2D centerHit = Physics2D.Raycast(targetPos + avgNormal * searchRadius, -avgNormal, searchRadius * 2.0f, attachableLayers);
        if (centerHit.collider != null)
        {
            float surfaceProg = Vector2.Dot(centerHit.point, avgNormal);
            float targetProg  = Vector2.Dot(targetPos, avgNormal);
            if (targetProg < surfaceProg)
            {
                // Lift the target position to be exactly at the surface before adding effectHeight
                targetPos += avgNormal * (surfaceProg - targetProg);
            }
        }

        
        // Scale body height and lead: 
        // We always want SOME height and lead to facilitate gait, even on webs.
        // If on ground OR a single continuous strand, we move more confidently (higher and faster)
        float confidence = Mathf.Max(groundFactor, strandFactor * 0.7f);
        
        float effectHeight = bodyHeight * Mathf.Lerp(0.2f, 1.0f, confidence);
        float effectLead   = bodyLead   * Mathf.Lerp(0.4f, 1.0f, confidence);
        
        targetPos += avgNormal * effectHeight;

        // 4. Input Lead: Have the head subtly lead the feet to drive the gait
        if (smoothedInput.sqrMagnitude > 0.01f)
        {
            // Project the lead onto the surface plane for stability
            targetPos += (Vector2)smoothedInput * effectLead;
        }

        // 5. Move the Body
        Vector2 toTarget = targetPos - headRb.position;
        
        // Stationary Stability & Surface-based damping
        float surfaceSmoothing = Mathf.Lerp(bodySmoothing * 0.7f, bodySmoothing, groundFactor);
        float currentSmoothing = (inputDirection.sqrMagnitude > 0.01f) ? surfaceSmoothing : surfaceSmoothing * 0.2f;
        
        Vector2 gravityCancel = -Physics2D.gravity * headRb.gravityScale;
        
        // Smoothly reach the calculated target
        headRb.linearVelocity = Vector2.Lerp(headRb.linearVelocity, toTarget * currentSmoothing, Time.fixedDeltaTime * 10f);
        headRb.AddForce(gravityCancel * headRb.mass, ForceMode2D.Force);

        // 5. Rotate the Body to match the surface
        headT.rotation = Quaternion.Slerp(headT.rotation, stableRot, Time.fixedDeltaTime * rotationSpeed);
        headRb.freezeRotation = true;

        // 6. Apply Weight to Webs
        // This transfers the spider's physical presence onto the strands so they sag and react.
        if (spiderWeightMult > 0.01f && planted > 0)
        {
            // Boosted force to actually move the strands against damping
            float weightPerLeg = (headRb.mass * Mathf.Abs(Physics2D.gravity.y) * spiderWeightMult * 2.0f) / planted;
            Vector2 weightDir = Physics2D.gravity.normalized;
            
            foreach (var leg in legs)
            {
                if (leg.isPlanted && leg.plantedTransform != null)
                {
                    // Use the plantedRb cached in the leg if possible, or fetch it
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

        // Detect if cursor is in grab range (Now based on leg reach)
        // CRITICAL: Only grab if the shooter is actually holding a live web
        if (!isManipulating && distToHead < legExtensionMax && shooter.IsHoldingWeb)
        {
            // Pick the closest front leg to grab the thread
            manipulatorLeg = legs[0]; 
            isManipulating = true;
            manipulatorLeg.isPlanted = false;

            if (shooter != null) shooter.SetTetherAnchor(manipulatorLeg.foot);
        }

        if (isManipulating)
        {
            // Override the leg to follow the cursor
            manipulatorLeg.currentFootPos = Vector2.Lerp(manipulatorLeg.currentFootPos, mousePos, Time.deltaTime * 20f);
            manipulatorLeg.isPlanted = false;

            // Check if we are touching a surface or existing web to "Stick" the web
            Collider2D col = Physics2D.OverlapCircle(manipulatorLeg.currentFootPos, 0.5f, attachableLayers | LayerMask.GetMask("Web"));
            
            bool shouldStick = false;
            if (col != null)
            {
                if (col.gameObject.layer == LayerMask.NameToLayer("Web"))
                {
                    WebSegment otherWs = col.GetComponent<WebSegment>();
                    if (otherWs != null && otherWs.stringId != shooter.stringId)
                        shouldStick = true;
                }
                else
                {
                    shouldStick = true; // Automatically stick to whatever non-web attachable surface we hit
                }
                
                if (Input.GetMouseButtonDown(0))
                    shouldStick = true;
            }

            if (shouldStick)
            {
                // Finalize the web at this location with explicit physical locking
                shooter.FinalizeExtrusion(col, manipulatorLeg.currentFootPos);
                shooter.SetTetherAnchor(null); 
                isManipulating = false;

                // Lock the foot to the surface/web we just stuck to
                manipulatorLeg.StepTo(manipulatorLeg.currentFootPos, currentSurfaceNormal, col);
                manipulatorLeg = null;
            }
            
            // If cursor leaves too far, release (Now based on leg reach)
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
