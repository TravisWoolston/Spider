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
    public float bodyHeight     = 0f;
    public float bodyHeightMin  = -5f;
    public float bodyHeightMax  = 10f;
    public float bodyHeightSpeed = 10f;
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
    [HideInInspector] public Vector2 inputDirection;
    private Vector2 smoothedInput;

    private List<AttachmentPoint> leftPoints  = new();
    private List<AttachmentPoint> rightPoints = new();
    private Vector2[] restingFootOffsets;
    private int nextLegIndex;
    private int lastSteppedIndex = -1;

    // Cached head transform — all orientation/position goes through this
    private Transform headT;

    // ───────────────────────────────────────────────────────────
    //  Lifecycle
    // ───────────────────────────────────────────────────────────

    void Start()
    {
        if (legs == null || legs.Length == 0)
            legs = GetComponentsInChildren<SpiderLegIK>();

        headRb = headRb != null ? headRb : GetComponent<Rigidbody2D>();
        headT  = headRb != null ? headRb.transform : transform;

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
                bool isRight = name.Contains("LEG R") || name.Contains("LEG_R");
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
        float rawH = Input.GetAxis("Horizontal");
        float rawV = Input.GetAxis("Vertical");

        // GLOBAL DIRECTION (Intuitive for 2D/2.5D Any-Direction movement)
        inputDirection = new Vector2(rawH, rawV);
        
        if (inputDirection.sqrMagnitude > 1f) 
            inputDirection.Normalize();
        else if (inputDirection.sqrMagnitude < 0.01f) 
            inputDirection = Vector2.zero;
            
        // Posture adjustment (Q/E or Shift/Ctrl)
        float heightInput = 0f;
        if (Input.GetKey(KeyCode.E) || Input.GetKey(KeyCode.LeftShift)) heightInput += 1f;
        if (Input.GetKey(KeyCode.Q) || Input.GetKey(KeyCode.LeftControl)) heightInput -= 1f;
        
        if (Input.GetKeyDown(KeyCode.R)) bodyHeight = 0f; // Quick reset to baseline

        bodyHeight += heightInput * bodyHeightSpeed * Time.deltaTime;
        bodyHeight = Mathf.Clamp(bodyHeight, bodyHeightMin, bodyHeightMax);
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

            RaycastHit2D hit = Physics2D.Raycast(origin, dir, sightDistance, attachableLayers);
            if (hit.collider == null) continue;

            float localX = headT.InverseTransformPoint(hit.point).x;
            var ap = new AttachmentPoint(hit.point, hit.normal);

            if (localX < 0) leftPoints.Add(ap);
            else            rightPoints.Add(ap);
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
                RaycastHit2D hit = Physics2D.Raycast(kneeOrigin, dir, sightDistance * 0.5f, attachableLayers);
                if (hit.collider == null) continue;

                float localX = headT.InverseTransformPoint(hit.point).x;
                var ap = new AttachmentPoint(hit.point, hit.normal);
                if (localX < 0) leftPoints.Add(ap);
                else            rightPoints.Add(ap);
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

        Vector2 headPos = headRb != null ? headRb.position : (Vector2)headT.position;

        // Where should this foot ideally be? Correctly accounts for head position/rotation/scale
        float maxReach = leg.thighLength + leg.shinLength;
        // Using smoothedInput for more fluid goal transitions
        Vector2 idealPos = (Vector2)headT.TransformPoint(restingFootOffsets[idx])
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
        int idx = System.Array.IndexOf(legs, leg);

        List<AttachmentPoint> pool = leg.isPhysicallyRight ? rightPoints : leftPoints;
        if (pool.Count == 0) return;

        Vector2 headPos = headRb != null ? headRb.position : (Vector2)headT.position;

        float maxReach = leg.thighLength + leg.shinLength;
        Vector2 idealPos = (Vector2)headT.TransformPoint(restingFootOffsets[idx])
                         + smoothedInput * maxReach * 0.4f;

        float comfortZone = maxReach * 0.9f;

        AttachmentPoint best = new AttachmentPoint();
        float bestScore = float.MinValue;
        bool foundAny = false;

        foreach (var ap in pool)
        {
            float distFromHead = Vector2.Distance(headPos, ap.point);
            if (distFromHead > legExtensionMax || distFromHead < legExtensionMin) continue;
            if (distFromHead < headSafeArea) continue;
            if (IsPointTooCloseToOtherFeet(ap.point, leg)) continue;

            // Ensure we take a minimum stride distance if moving
            if (inputDirection.sqrMagnitude > 0.01f)
            {
                if (Vector2.Distance(leg.currentFootPos, ap.point) < minStride)
                    continue;
            }

            float localX = headT.InverseTransformPoint(ap.point).x;

            float score = 0f;

            // 1. Distance to predictive goal (keep it close to the ideal arc)
            score -= Vector2.Distance(idealPos, ap.point) * 2.0f;
            
            // 2. Center-line avoidance (HARD FILTER: Don't cross the body)
            float sideBias = leg.isPhysicallyRight ? 1f : -1f;
            if (localX * sideBias < 0.1f) continue; 

            // 4. Directional Progressive Check (HARD FILTER)
            // Ensure we don't step backwards relative to the input
            if (smoothedInput.sqrMagnitude > 0.05f)
            {
                Vector2 stepVec = (ap.point - leg.currentFootPos);
                float progress = Vector2.Dot(stepVec, smoothedInput.normalized);
                if (progress < -minStride * 0.1f) continue; 
            }

            // 3. Extension preference (favor points near the limit for maximum reach)
            float extDiff = Mathf.Abs(distFromHead - comfortZone);
            score -= extDiff * 1.5f;

            // 5. Huge Step Penalty: Discourage jumping from one extreme to the other in 180s
            float moveDist = Vector2.Distance(leg.currentFootPos, ap.point);
            if (moveDist > maxReach * 1.2f) score -= 10f;

            // 4. Directional consistency (maximize progress along the intended path)
            if (smoothedInput.sqrMagnitude > 0.05f)
            {
                // Calculate toPoint relative to the leg's shoulder/rest position 
                // to maintain quadrant integrity.
                Vector2 shoulderPos = (Vector2)headT.TransformPoint(restingFootOffsets[idx]);
                Vector2 toPointRel = ap.point - shoulderPos;
                Vector2 inputNorm = smoothedInput.normalized;
                
                // Score based on how far a point is "ahead" relative to the leg's shoulder
                float projection = Vector2.Dot(toPointRel, inputNorm);
                score += projection * 5f; 
                
                // General directional alignment
                score += Vector2.Dot(toPointRel.normalized, inputNorm) * 8f;
            }

            // 5. Normal Alignment: Favor points that "spread" the legs (local X) rather than just being under the body (local Y)
            // localX is distance from the center line. localY is distance "above/below" the spider.
            // We want to favor high localX and minimize localY magnitude (proximity to the spider's plane)
            float localY = headT.InverseTransformPoint(ap.point).y;
            score += Mathf.Abs(localX) * 2f;      // Favor width
            score -= Mathf.Abs(localY) * 1.5f;    // Penalize being too far "above" or "below" the spider's main plane
            
            // 6. Surface Normal Bonus: Favor surfaces facing "up" relative to the spider (parallel to its belly)
            score += Vector2.Dot(ap.normal, headT.up) * 3f;

            if (score > bestScore) { bestScore = score; best = ap; foundAny = true; }
        }

        if (foundAny)
            leg.StepTo(best.point, best.normal);
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
            if (distFromHead < headSafeArea) continue;

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

        // 4. Move the Body
        Vector2 toTarget = targetPos - headRb.position;
        
        // Stationary Stability: Be "lazy" when no input is given to keep head still
        float currentSmoothing = (inputDirection.sqrMagnitude > 0.05f) ? bodySmoothing : bodySmoothing * 0.3f;
        
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
