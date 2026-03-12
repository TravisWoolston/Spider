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
    public float sightDistance  = 15f;

    [Header("Debug")]
    public bool showDebug = true;

    // ── Runtime state ──────────────────────────────────────────
    private float targetBodyHeight = 0f;
    [HideInInspector] public Vector2 inputDirection;
    private Vector2 smoothedInput;

    private List<AttachmentPoint> leftPoints  = new();
    private List<AttachmentPoint> rightPoints = new();
    private Vector2[] restingFootOffsets;
    private int nextLegIndex;
    private int lastSteppedIndex = -1;

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
                        leg.UpdateStepTarget(best.point, best.normal);
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
            // If pushing stick "away" from surface, hunker down (Dodging)
            // If pushing stick "towards" surface, rise up
            Vector2 surfaceNormal = (targetBodyHeight > 0) ? (Vector2)headT.up : -(Vector2)headT.up;
            float dodgeBias = Vector2.Dot(smoothedInput, (Vector2)headT.up);
            
            // Auto Reach: If moving into empty space, hunker
            int pointsAhead = 0;
            foreach (var ap in leftPoints) 
                if (Vector2.Dot(ap.point - (Vector2)headT.position, smoothedInput.normalized) > 0.5f) pointsAhead++;
            foreach (var ap in rightPoints) 
                if (Vector2.Dot(ap.point - (Vector2)headT.position, smoothedInput.normalized) > 0.5f) pointsAhead++;

            if (pointsAhead < 2) 
                targetBodyHeight = Mathf.Lerp(targetBodyHeight, bodyHeightMin, Time.deltaTime * 3f);
            else
                targetBodyHeight = Mathf.MoveTowards(targetBodyHeight, defaultBodyHeight + dodgeBias * 3f, Time.deltaTime * 3f);
        }
        else
        {
            targetBodyHeight = Mathf.MoveTowards(targetBodyHeight, defaultBodyHeight, Time.deltaTime * 2f);
        }

        targetBodyHeight = Mathf.Clamp(targetBodyHeight, bodyHeightMin, bodyHeightMax);
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

        float step = 360f / rayCount;
        for (int i = 0; i < rayCount; i++)
        {
            float rad = i * step * Mathf.Deg2Rad;
            Vector2 dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));

            // Use RaycastAll so we can "See through" nearby spiders-web
            RaycastHit2D[] hits = Physics2D.RaycastAll(origin, dir, sightDistance, attachableLayers);
            foreach (var hit in hits)
            {
                if (hit.collider == null) continue;

                float dist = Vector2.Distance(hit.point, origin);
                // Walking: Only ignore webs that are RIGHT next to the head (spinneret)
                // Use a small constant like 1f or headSafeArea for walking
                if (hit.collider.gameObject.layer == LayerMask.NameToLayer("Web") && dist < 1.0f)
                    continue;

                float localX = headT.InverseTransformPoint(hit.point).x;
                var ap = new AttachmentPoint(hit.point, hit.normal, hit.collider);

                if (localX < 0) leftPoints.Add(ap);
                else            rightPoints.Add(ap);
                
                // Once we find the first valid surface (usually the floor or a far web), stop looking in this direction
                break;
            }
        }

        // --- Knee-based Raycasting for local obstacle/cliff detection ---
        foreach (var leg in legs)
        {
            Vector2 kneeOrigin = leg.GetKneePosition();
            float kneeRayCount = 8;
            float kneeStep = 360f / kneeRayCount;
            for (int i = 0; i < kneeRayCount; i++)
            {
                float rad = i * kneeStep * Mathf.Deg2Rad;
                Vector2 dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
                RaycastHit2D[] hits = Physics2D.RaycastAll(kneeOrigin, dir, sightDistance * 0.5f, attachableLayers);
                foreach (var hit in hits)
                {
                    if (hit.collider == null) continue;
                    float dist = Vector2.Distance(hit.point, origin);
                    if (hit.collider.gameObject.layer == LayerMask.NameToLayer("Web") && dist < 1.0f)
                        continue;

                    float localX = headT.InverseTransformPoint(hit.point).x;
                    var ap = new AttachmentPoint(hit.point, hit.normal, hit.collider);
                    if (localX < 0) leftPoints.Add(ap);
                    else            rightPoints.Add(ap);
                    break;
                }
            }
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
        for (int i = 0; i < legs.Length; i++)
            restingFootOffsets[i] = headT.InverseTransformPoint(legs[i].currentFootPos);
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
            if (distFromHead > legExtensionMax || distFromHead < legExtensionMin) continue;
            // Basic safety: Don't step inside the head radius (keeping this small for actual stepping)
            if (distFromHead < headSafeArea) continue; 
            if (IsPointTooCloseToOtherFeet(ap.point, leg)) continue;

            // Significant stride check ONLY for the start of a step
            if (!leg.isStepping && inputDirection.sqrMagnitude > 0.01f)
            {
                if (Vector2.Distance(leg.currentFootPos, ap.point) < minStride)
                    continue;
            }

            float localX = headT.InverseTransformPoint(ap.point).x;
            float score = 0f;

            // 1. Distance to predictive goal
            score -= Vector2.Distance(idealPos, ap.point) * 2.0f;
            
            // 2. Center-line avoidance (HARD FILTER)
            float sideBias = leg.isPhysicallyRight ? 1f : -1f;
            if (localX * sideBias < 0.1f) continue; 

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

            // 5. Normal Alignment
            float localY = headT.InverseTransformPoint(ap.point).y;
            score += Mathf.Abs(localX) * 2f;
            score -= Mathf.Abs(localY) * 1.5f;
            score += Vector2.Dot(ap.normal, headT.up) * 3f;

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
            if (distFromHead > legExtensionMax || distFromHead < legExtensionMin) continue;
            if (distFromHead < headSafeArea) continue; // Keep stepping safety separate from web safety

            // Longitudinal check (side detection)
            float localX = headT.InverseTransformPoint(ap.point).x;
            float sideBias = leg.isPhysicallyRight ? 1f : -1f;
            if (localX * sideBias < 0.1f) continue;

            if (IsPointTooCloseToOtherFeet(ap.point, leg)) continue;

            // Must be a significant step
            float stepDist = Vector2.Distance(leg.currentFootPos, ap.point);
            if (stepDist < minStride) continue;

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

        int planted = 0;
        Vector2 avgFootPos = Vector2.zero;
        Vector2 normalSum  = Vector2.zero;

        // 1. Gather status from planted legs
        foreach (var leg in legs)
        {
            if (!leg.isPlanted) continue;
            
            avgFootPos   += leg.currentFootPos;
            
            // Sample normal at each foot for a stable average surface orientation
            RaycastHit2D hit = Physics2D.Raycast(leg.currentFootPos + (Vector2)headT.up * 0.2f, -headT.up, 1f, attachableLayers);
            normalSum += (hit.collider != null) ? hit.normal : (Vector2)headT.up;
            
            planted++;
        }

        if (planted == 0)
        {
            headRb.freezeRotation = false; // Fall naturally
            return;
        }

        Vector2 avgNormal = (normalSum / planted).normalized;
        
        // 2. Determine "Stable Frame" (Ideal rotation based on surface)
        float targetAngle = Mathf.Atan2(avgNormal.y, avgNormal.x) * Mathf.Rad2Deg - 90f;
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
        
        Vector2 targetPos = (posSum / planted) + avgNormal * bodyHeight;

        // 4. Input Lead: Have the head subtly lead the feet to drive the gait
        if (smoothedInput.sqrMagnitude > 0.01f)
        {
            // Project the lead onto the surface plane for stability
            targetPos += (Vector2)smoothedInput * bodyLead;
        }

        // 5. Move the Body
        Vector2 toTarget = targetPos - headRb.position;
        
        // Stationary Stability: Be much more "lazy" when stopped to prevent bobbing
        float currentSmoothing = (inputDirection.sqrMagnitude > 0.01f) ? bodySmoothing : bodySmoothing * 0.2f;
        
        Vector2 gravityCancel = -Physics2D.gravity * headRb.gravityScale;
        
        // Smoothly reach the calculated target
        headRb.linearVelocity = Vector2.Lerp(headRb.linearVelocity, toTarget * currentSmoothing, Time.fixedDeltaTime * 10f);
        headRb.AddForce(gravityCancel * headRb.mass, ForceMode2D.Force);

        // 5. Rotate the Body to match the surface
        headT.rotation = Quaternion.Slerp(headT.rotation, stableRot, Time.fixedDeltaTime * rotationSpeed);
        headRb.freezeRotation = true;
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
                manipulatorLeg.StepTo(manipulatorLeg.currentFootPos, headT.up, col);
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

        Gizmos.color = Color.yellow;
        Gizmos.DrawRay(headPos, inputDirection * 3f);

        Gizmos.color = new Color(0, 1, 0, 0.08f);
        Gizmos.DrawWireSphere(headPos, legExtensionMax);
        Gizmos.DrawWireSphere(headPos, legExtensionMin);
        Gizmos.color = new Color(1, 0, 0, 0.1f);
        Gizmos.DrawWireSphere(headPos, headSafeArea);
    }
}
