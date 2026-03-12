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
    public float headSafeArea  = 3f;

    [Header("Body")]
    public Rigidbody2D headRb;
    public float bodySmoothing  = 10f;
    public float bodyHeight     = 4f;
    public float bodyHeightMin  = 1f;
    public float bodyHeightMax  = 10f;
    public float bodyHeightSpeed = 5f;
    public float rotationSpeed  = 5f;
    public float stepCooldown    = 0.3f;
    public float walkLean        = 3f;
    public LayerMask attachableLayers;

    [Header("Raycasting")]
    public int   rayCount      = 100;
    public float sightDistance  = 15f;

    [Header("Debug")]
    public bool showDebug = true;

    // ── Runtime state ──────────────────────────────────────────
    [HideInInspector] public Vector2 inputDirection;

    private List<AttachmentPoint> leftPoints  = new();
    private List<AttachmentPoint> rightPoints = new();
    private Vector2[] restingFootOffsets;
    private int nextLegIndex;

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

        // Movement along the spider's local RIGHT (surface tangent)
        inputDirection = ((Vector2)headT.right * rawH).normalized * Mathf.Abs(rawH);

        // Vertical input adjusts body height
        if (Mathf.Abs(rawV) > 0.1f)
        {
            bodyHeight += rawV * bodyHeightSpeed * Time.deltaTime;
            bodyHeight = Mathf.Clamp(bodyHeight, bodyHeightMin, bodyHeightMax);
        }
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
            if (leg.plantedTimer < stepCooldown) continue;

            if (leg.isPhysicallyRight && detachedR >= Mathf.CeilToInt(maxDetached / 2f)) continue;
            if (!leg.isPhysicallyRight && detachedL >= Mathf.CeilToInt(maxDetached / 2f)) continue;

            float u = Urgency(leg);
            if (u > maxUrg) { maxUrg = u; best = leg; }
        }

        if (best != null) StepLeg(best);
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

        // Where should this foot ideally be?
        float maxReach = leg.thighLength + leg.shinLength;
        Vector2 idealPos = headPos
                         + (Vector2)headT.TransformDirection(restingFootOffsets[idx])
                         + inputDirection * maxReach * 0.15f;

        float distFromIdeal = Vector2.Distance(leg.currentFootPos, idealPos);
        float idealUrgency = distFromIdeal / (maxReach * 0.4f);

        float ext = leg.GetExtension() / legExtensionMax;

        float proximityUrgency = 0f;
        foreach (var other in legs)
        {
            if (other == leg || !other.isPlanted) continue;
            float d = Vector2.Distance(leg.currentFootPos, other.currentFootPos);
            if (d < footSpacing * 3f)
                proximityUrgency += 1f - (d / (footSpacing * 3f));
        }

        return Mathf.Max(idealUrgency, ext) + proximityUrgency * 0.3f;
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
        Vector2 idealPos = headPos
                         + (Vector2)headT.TransformDirection(restingFootOffsets[idx])
                         + inputDirection * maxReach * 0.15f;

        float comfortZone = maxReach * 0.85f;

        AttachmentPoint best = new AttachmentPoint();
        float bestScore = float.MinValue;
        bool foundAny = false;

        foreach (var ap in pool)
        {
            float distFromHead = Vector2.Distance(headPos, ap.point);
            if (distFromHead > legExtensionMax || distFromHead < legExtensionMin) continue;
            if (distFromHead < headSafeArea) continue;
            if (IsPointTooCloseToOtherFeet(ap.point, leg)) continue;

            float localX = headT.InverseTransformPoint(ap.point).x;

            float score = 0f;

            score -= Vector2.Distance(idealPos, ap.point) * 1.5f;
            score += Mathf.Abs(localX) * 3f;

            float extDiff = Mathf.Abs(distFromHead - comfortZone);
            score -= extDiff * 1.5f;

            if (inputDirection.sqrMagnitude > 0.01f)
                score += Vector2.Dot((ap.point - headPos).normalized, inputDirection) * 2f;

            score += Vector2.Dot(ap.normal, headT.up) * 2f;

            if (score > bestScore) { bestScore = score; best = ap; foundAny = true; }
        }

        if (foundAny)
            leg.StepTo(best.point, best.normal);
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
        if (restingFootOffsets == null) return;

        int planted = 0;
        Vector2 posSum    = Vector2.zero;
        Vector2 normalSum = Vector2.up;

        for (int i = 0; i < legs.Length; i++)
        {
            if (!legs[i].isPlanted) continue;

            Vector2 localOff = restingFootOffsets[i];
            Vector2 worldOff = headT.TransformDirection(localOff);
            Vector2 ideal    = legs[i].currentFootPos - worldOff + (Vector2)headT.up * bodyHeight;
            posSum += ideal;

            RaycastHit2D hit = Physics2D.Raycast(
                legs[i].currentFootPos + (Vector2)headT.up * 0.5f,
                -headT.up, 2f, attachableLayers);
            if (hit.collider != null) normalSum += hit.normal;

            planted++;
        }

        if (planted > 0)
        {
            Vector2 target = posSum / planted;

            // Lean body slightly in walking direction
            target += inputDirection * walkLean;

            Vector2 toTarget = target - headRb.position;

            // Compute velocity that reaches target AND cancels gravity.
            // Without this, gravity pulls the spider off walls/ceilings.
            Vector2 gravityCancel = -Physics2D.gravity * headRb.gravityScale;
            Vector2 desiredVel = toTarget * bodySmoothing;

            headRb.linearVelocity = desiredVel;
            // Cancel gravity via force so velocity stays clean
            headRb.AddForce(gravityCancel * headRb.mass, ForceMode2D.Force);

            AlignToGround(normalSum.normalized);
        }
        else
        {
            // No feet planted — gravity takes over naturally
            headRb.freezeRotation = false;
        }
    }

    void AlignToGround(Vector2 normal)
    {
        // Align the spider's "up" to the surface normal.
        // This makes the spider orient correctly on floors, walls, and ceilings.
        float angle = Mathf.Atan2(normal.y, normal.x) * Mathf.Rad2Deg - 90f;
        Quaternion target = Quaternion.Euler(0, 0, angle);
        headT.rotation = Quaternion.Slerp(headT.rotation, target, Time.fixedDeltaTime * rotationSpeed);
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

                if (inputDirection.sqrMagnitude > 0.01f)
                {
                    float maxReach = leg.thighLength + leg.shinLength;
                    Vector2 ideal = headPos
                                  + (Vector2)t.TransformDirection(restingFootOffsets[i])
                                  + inputDirection * maxReach * 0.15f;
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
