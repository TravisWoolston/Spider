using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Optimized spider leg with multi-factor target scoring, arc-based foot
/// trajectories, and analytical IK. Pair with SpiderController2 on the body.
/// 
/// Expects the same HingeJoint2D hierarchy as SpiderLeg:
///   legSegment1 → 1 HingeJoint2D (connects upper leg to body)
///   legSegment2 → 2 HingeJoint2Ds ([0] = elbow, [1] = foot attachment)
/// </summary>
public class SpiderLeg2 : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Transform legSegment1;
    [SerializeField] private Transform legSegment2;
    [SerializeField] private SpiderController2 controller;

    [Header("Configuration")]
    [SerializeField] private bool isLeftSide;

    // Auto-detected lengths
    private float lengthUpper;
    private float lengthLower;

    // Resolved at Awake
    private HingeJoint2D hingeUpper;  // segment1 ↔ body
    private HingeJoint2D hingeLower;  // segment2 ↔ segment1
    private HingeJoint2D hingeFoot;   // segment2 ↔ surface
    private Rigidbody2D rbUpper;
    private Rigidbody2D rbLower;

    // Step state
    private bool isAttached;
    private Vector2 currentTarget;
    private Vector2 currentNormal;
    private Vector2 stepStartPos;
    private float stepTimer;
    private float reassignTimer;
    private List<AttachmentPoint> availablePoints = new();

    // Public read-only
    public bool IsAttached => isAttached;
    public bool IsLeftSide => isLeftSide;
    public Vector2 CurrentTarget => currentTarget;
    public Vector2 FootPosition =>
        (Vector2)legSegment2.TransformPoint(hingeFoot.anchor);

    // ─── Lifecycle ───────────────────────────────────────────

    void Awake()
    {
        // Auto-find controller
        if (controller == null)
            controller = GetComponentInParent<SpiderController2>();

        // Auto-find segments
        // Assuming: Leg -> Segment1 -> Segment2
        if (legSegment1 == null)
        {
            if (transform.childCount > 0)
                legSegment1 = transform.GetChild(0);
        }

        if (legSegment1 != null && legSegment2 == null)
        {
            if (legSegment1.childCount > 0)
                legSegment2 = legSegment1.GetChild(0);
        }

        if (legSegment1 == null || legSegment2 == null)
        {
            Debug.LogError($"[{gameObject.name}] Could not auto-find segments! Please assign manually.");
            return;
        }

        hingeUpper = legSegment1.GetComponent<HingeJoint2D>();
        var hinges = legSegment2.GetComponents<HingeJoint2D>();
        
        if (hinges.Length < 2)
        {
            Debug.LogError($"[{gameObject.name}] Segment2 must have at least 2 HingeJoint2Ds!");
            return;
        }

        hingeLower = hinges[0];
        hingeFoot  = hinges[1];
        rbUpper = legSegment1.GetComponent<Rigidbody2D>();
        rbLower = legSegment2.GetComponent<Rigidbody2D>();

        // Auto-detect lengths based on hinge anchors
        // lengthUpper is the distance from shoulder anchor to elbow anchor (in local upper segment space)
        lengthUpper = Vector2.Distance(hingeUpper.anchor, hingeLower.connectedAnchor);
        // lengthLower is the distance from elbow anchor to foot tip anchor (in local lower segment space)
        lengthLower = Vector2.Distance(hingeLower.anchor, hingeFoot.anchor);

        hingeFoot.enabled = false;
    }

    void FixedUpdate()
    {
        if (!isAttached)
        {
            DriveTowardsTarget();
            reassignTimer += Time.fixedDeltaTime;
            if (reassignTimer > controller.TargetReassignTime)
            {
                reassignTimer = 0f;
                PickTarget(controller.InputDirection);
            }
        }
    }

    void Update()
    {
        if (isAttached)
        {
            if (controller.InputDirection != Vector2.zero)
            {
                PushBody();
                CheckExtension();
            }
            else
            {
                LockLeg();
            }
        }
        else
        {
            TryAttach();
        }
    }

    // ─── Public API ──────────────────────────────────────────

    public void SetAvailablePoints(List<AttachmentPoint> points)
    {
        availablePoints = points;
    }

    /// <summary>Called by controller on Start to get an initial foothold.</summary>
    public void FindInitialTarget()
    {
        stepStartPos = FootPosition; // so the initial arc doesn't start from (0,0)
        stepTimer = 0f;
        PickTarget(Vector2.down); // default: look downward for ground
        if (currentTarget == Vector2.zero)
            PickTarget(Vector2.zero); // fallback: closest point
    }

    /// <summary>
    /// Returns how urgently this leg needs to step. Higher = more urgent.
    /// Only meaningful for attached legs.
    /// </summary>
    public float GetStepUrgency(Vector2 inputDir)
    {
        if (!isAttached || inputDir == Vector2.zero) return 0f;

        float legReach = controller.MaxLegExtension;
        Vector2 headPos = controller.Head.position;
        Vector2 footPos = FootPosition;

        // How stretched is this leg? (0 = at body, 1 = fully extended)
        float stretch = Vector2.Distance(headPos, footPos) / legReach;

        // Is the foot behind the body relative to movement?
        Vector2 footDir = (footPos - headPos).normalized;
        float behindness = Mathf.Max(0f, -Vector2.Dot(footDir, inputDir));

        // Too close to body? (cramped)
        float crampedness = Mathf.Max(0f, 1f - stretch * 3f);

        return stretch * 2f + behindness * 3f + crampedness * 1.5f;
    }

    /// <summary>Called by controller when this leg should take a step.</summary>
    public void BeginStep(Vector2 inputDir)
    {
        // Release foot
        hingeFoot.connectedBody = null;
        hingeFoot.enabled = false;
        isAttached = false;

        // Record arc start
        stepStartPos = FootPosition;
        stepTimer = 0f;
        reassignTimer = 0f;

        // Unlock lower segment for free aiming
        hingeLower.useLimits = false;
        hingeLower.useMotor = false;

        PickTarget(inputDir);
    }

    public void ReleaseFoot()
    {
        if (!isAttached) return;
        hingeFoot.connectedBody = null;
        hingeFoot.enabled = false;
        isAttached = false;
    }

    // ─── Target Selection (multi-factor scoring) ─────────────

    private void PickTarget(Vector2 inputDir)
    {
        // Predict future head position
        float predTime = controller.StepDuration;
        Vector2 futureHead = (Vector2)controller.Head.position
            + inputDir * controller.FootSpeed * predTime * 0.05f;

        float legReach = controller.MaxLegExtension;
        Vector2 knee = legSegment1.TransformPoint(hingeLower.connectedAnchor);

        AttachmentPoint best = default;
        float bestScore = float.MinValue;

        foreach (var ap in availablePoints)
        {
            float s = ScorePoint(ap, inputDir, futureHead, legReach, knee);
            if (s > bestScore) { bestScore = s; best = ap; }
        }

        if (best.point != Vector2.zero)
        {
            currentTarget = best.point;
            currentNormal = best.normal;
        }
    }

    private float ScorePoint(AttachmentPoint ap, Vector2 inputDir,
                             Vector2 futureHead, float legReach, Vector2 knee)
    {
        Vector2 pt = ap.point;

        // Hard constraints
        float distHead = Vector2.Distance(futureHead, pt);
        if (distHead > legReach || distHead < controller.HeadSafeArea) return float.MinValue;
        if (Vector2.Distance(knee, pt) < lengthLower * 0.5f)       return float.MinValue;

        // 1. Advance: does this step move us forward?
        float advance = Vector2.Dot((pt - futureHead).normalized, inputDir);

        // 2. Comfort: sweet spot at ~70% reach
        float comfort = 1f - Mathf.Abs(distHead / legReach - 0.7f) * 3f;

        // 3. Grip: surface normal should face toward body
        float grip = Vector2.Dot(ap.normal, (futureHead - pt).normalized);

        // 4. Spacing: don't cluster feet together
        float spacing = Mathf.Clamp01(
            controller.MinDistToOtherFeet(pt, this) / (legReach * 0.5f));

        // 5. Correct side of body
        Vector2 local = controller.Head.InverseTransformPoint(pt);
        float side = isLeftSide ? (local.x < 0 ? 1f : 0.1f)
                                : (local.x > 0 ? 1f : 0.1f);

        return advance * 3f + comfort * 2f + grip * 1.5f + spacing * 1f + side * 2f;
    }

    // ─── Foot Movement (lift → reach phases) ───────────────────

    private void DriveTowardsTarget()
    {
        if (currentTarget == Vector2.zero) return;

        stepTimer += Time.fixedDeltaTime;
        float t = Mathf.Clamp01(stepTimer / controller.StepDuration);

        Vector2 footPos = FootPosition;
        Vector2 dirToTarget = (currentTarget - footPos).normalized;
        float distToTarget = Vector2.Distance(footPos, currentTarget);

        // Arc geometry (used for lift phase aiming and gizmos)
        float arcH = controller.MaxLegExtension * controller.StepArcHeight;
        Vector2 mid = (stepStartPos + currentTarget) / 2f
                    + (Vector2)controller.Head.up * arcH;

        if (t < 0.35f)
        {
            // ── LIFT PHASE ──
            // Upper segment: IK drives knee upward along the arc
            Vector2 arcPos = Bezier(stepStartPos, mid, currentTarget, t);
            DriveUpperSegment(arcPos);

            // Lower segment: aim at the arc midpoint (keeps foot lifting)
            AimLowerSegment(mid);
        }
        else
        {
            // ── REACH PHASE ──
            // Upper segment: IK drives knee toward surface target
            // (constant target = no motor reversal, unlike the arc peak)
            // DriveUpperSegment(currentTarget);

            // Lower segment: aim directly at the surface target
            AimLowerSegment(currentTarget);

            // Magnetic force pulls foot toward target surface
            float forceMag = controller.MagnetForce;
            if (t >= 1f) forceMag *= 3f; // much stronger when overdue
            rbLower.AddForce(dirToTarget * forceMag);
        }
    }

    private void DriveUpperSegment(Vector2 target)
    {
        // Analytical IK: compute desired shoulder angle for 2-bone chain
        Vector2 shoulder = legSegment1.position;
        float L1 = lengthUpper, L2 = lengthLower;

        Vector2 toTarget = target - shoulder;
        float dist = Mathf.Clamp(toTarget.magnitude,
            Mathf.Abs(L1 - L2) + 0.01f, L1 + L2 - 0.01f);

        float baseAngle = Mathf.Atan2(toTarget.y, toTarget.x) * Mathf.Rad2Deg;

        // Law of cosines for shoulder offset
        float cosShoulder = (L1 * L1 + dist * dist - L2 * L2) / (2f * L1 * dist);
        float shoulderOffset = Mathf.Acos(Mathf.Clamp(cosShoulder, -1f, 1f)) * Mathf.Rad2Deg;

        float targetAngle = isLeftSide
            ? baseAngle + shoulderOffset
            : baseAngle - shoulderOffset;

        float diff = Mathf.DeltaAngle(legSegment1.eulerAngles.z, targetAngle);
        SetMotor(hingeUpper, diff * controller.RotationSpeed);
    }

    private void AimLowerSegment(Vector2 target)
    {
        Vector2 dir = (target - FootPosition).normalized;
        float targetAngle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
        float speed = controller.RotationSpeed * Time.fixedDeltaTime;
        rbLower.MoveRotation(
            Mathf.LerpAngle(rbLower.rotation, targetAngle, speed));
    }

    // ─── Attached Behavior ───────────────────────────────────

    private void PushBody()
    {
        // Foot hinge motor pushes body in input direction
        Vector2 localInput = controller.Head
            .InverseTransformDirection(controller.InputDirection).normalized;
        Vector2 worldDir = controller.Head.TransformDirection(localInput);

        float target = Mathf.Atan2(worldDir.y, worldDir.x) * Mathf.Rad2Deg;
        float diff = Mathf.DeltaAngle(legSegment2.eulerAngles.z, target);

        SetMotor(hingeFoot, diff * controller.FootSpeed);
        hingeLower.useMotor = false;
        hingeUpper.useMotor = false;
    }

    private void CheckExtension()
    {
        float dist = Vector2.Distance(controller.Head.position, FootPosition);
        if (dist > controller.MaxLegExtension || dist < controller.HeadSafeArea)
            BeginStep(controller.InputDirection);
    }

    private void LockLeg()
    {
        SetMotor(hingeFoot, 0f);
        hingeUpper.useMotor = false;
    }

    // ─── Attachment ──────────────────────────────────────────

    private void TryAttach()
    {
        if (currentTarget == Vector2.zero) return;

        // Gate: the PHYSICAL hinge position must be within the attach radius of the target
        Vector2 hingeWorldPos = hingeFoot.transform.position;
        if (Vector2.Distance(hingeWorldPos, currentTarget) > controller.AttachRadius)
            return;

        // All gates passed — attach (use prefab anchor, don't override it)
        hingeFoot.connectedBody = null;
        hingeFoot.enabled = true;

        // Find the closest EXTERNAL rigidbody to connect to
        LayerMask surfaceMask = LayerMask.GetMask("Default", "Web");
        Transform spiderRoot = controller.transform.root;
        Vector2 footPos = FootPosition;
        var hits = Physics2D.OverlapCircleAll(
            footPos, controller.SearchRadius, surfaceMask);

        Rigidbody2D closest = null;
        float closestDist = float.MaxValue;
        foreach (var col in hits)
        {
            if (col.transform.root == spiderRoot) continue; // skip self
            var rb = col.attachedRigidbody;
            if (rb == null) continue;
            float d = Vector2.Distance(footPos, rb.position);
            if (d < closestDist) { closestDist = d; closest = rb; }
        }

        if (closest != null)
            hingeFoot.connectedBody = closest;

        isAttached = true;
        hingeLower.useLimits = false;

        // Debug: log attachment details
        string bodyName = closest != null ? closest.gameObject.name : "WORLD (null rb)";
        float hingeToTarget = Vector2.Distance(hingeWorldPos, currentTarget);
        Debug.Log($"[{gameObject.name}] ATTACHED to \"{bodyName}\" | " +
                  $"hingePos={hingeWorldPos} target={currentTarget} foot={footPos} | " +
                  $"hinge→target={hingeToTarget:F2} " +
                  $"anchor={hingeFoot.anchor} connAnchor={hingeFoot.connectedAnchor}");
    }

    // ─── Utilities ───────────────────────────────────────────

    private void SetMotor(HingeJoint2D hinge, float speed)
    {
        var motor = hinge.motor;
        motor.motorSpeed = speed;
        motor.maxMotorTorque = controller.MaxMotorTorque;
        hinge.motor = motor;
        hinge.useMotor = true;
    }

    private static Vector2 Bezier(Vector2 a, Vector2 b, Vector2 c, float t)
    {
        float u = 1f - t;
        return u * u * a + 2f * u * t * b + t * t * c;
    }

    // ─── Debug Gizmos ────────────────────────────────────────

    void OnDrawGizmos()
    {
        if (controller == null || !controller.ShowDebug || !Application.isPlaying) return;

        Vector2 footPos = FootPosition;

        // Current target (blue sphere)
        if (currentTarget != Vector2.zero)
        {
            Gizmos.color = Color.blue;
            Gizmos.DrawSphere(currentTarget, 0.2f);

            // Attach radius circle at target
            Gizmos.color = new Color(0.3f, 0.3f, 1f, 0.25f);
            DrawCircle(currentTarget, controller.AttachRadius, 16);
        }

        // Foot position (green = attached, yellow = detached)
        Gizmos.color = isAttached ? Color.green : Color.yellow;
        Gizmos.DrawSphere(footPos, 0.18f);

        // Line from foot to target
        if (currentTarget != Vector2.zero)
        {
            Gizmos.color = isAttached ? Color.green : Color.magenta;
            Gizmos.DrawLine(footPos, currentTarget);
        }

        // Lower segment rectangle
        if (legSegment2 != null)
        {
            Gizmos.color = isAttached ? Color.green : Color.cyan;
            DrawSegmentRect(legSegment2.position, lengthLower, 0.37f, legSegment2.eulerAngles.z);
        }

        // Arc trajectory when stepping (magenta curve)
        if (!isAttached && currentTarget != Vector2.zero && stepStartPos != Vector2.zero)
        {
            Gizmos.color = new Color(1f, 0.4f, 1f, 0.6f);
            float arcH = controller.MaxLegExtension * controller.StepArcHeight;
            Vector2 mid = (stepStartPos + currentTarget) / 2f
                        + (Vector2)controller.Head.up * arcH;

            Vector2 prev = stepStartPos;
            for (int i = 1; i <= 20; i++)
            {
                float st = i / 20f;
                Vector2 pt = Bezier(stepStartPos, mid, currentTarget, st);
                Gizmos.DrawLine(prev, pt);
                prev = pt;
            }

            // Current arc progress marker (white)
            float t = Mathf.Clamp01(stepTimer / controller.StepDuration);
            Vector2 arcNow = Bezier(stepStartPos, mid, currentTarget, t);
            Gizmos.color = Color.white;
            Gizmos.DrawSphere(arcNow, 0.12f);
        }

        // Surface contact check radius at foot (orange when detached)
        if (!isAttached)
        {
            Gizmos.color = new Color(1f, 0.6f, 0f, 0.3f);
            DrawCircle(footPos, 0.3f, 12);
        }
    }

    private static void DrawCircle(Vector2 center, float radius, int segments)
    {
        float step = 360f / segments;
        for (int i = 0; i < segments; i++)
        {
            float a1 = i * step * Mathf.Deg2Rad;
            float a2 = (i + 1) * step * Mathf.Deg2Rad;
            Vector2 p1 = center + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * radius;
            Vector2 p2 = center + new Vector2(Mathf.Cos(a2), Mathf.Sin(a2)) * radius;
            Gizmos.DrawLine(p1, p2);
        }
    }

    private static void DrawSegmentRect(Vector3 pos, float length, float width, float angle)
    {
        Vector3 offset = new Vector3(length / 2, 0, 0);
        Quaternion rot = Quaternion.Euler(0, 0, angle);
        Vector3 p1 = pos + rot * (offset + new Vector3(0, width / 2, 0));
        Vector3 p2 = pos + rot * (offset - new Vector3(0, width / 2, 0));
        Vector3 p3 = pos + rot * (-offset - new Vector3(0, width / 2, 0));
        Vector3 p4 = pos + rot * (-offset + new Vector3(0, width / 2, 0));
        Gizmos.DrawLine(p1, p2);
        Gizmos.DrawLine(p2, p3);
        Gizmos.DrawLine(p3, p4);
        Gizmos.DrawLine(p4, p1);
    }
}
