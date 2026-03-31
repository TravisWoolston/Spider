using UnityEngine;
using System.Collections;

public class SpiderLegIK : MonoBehaviour
{
    // ── Hierarchy ──────────────────────────────────────────────
    // R1 (thigh) = this.transform — sprite with pivot at CENTER
    // R2 (shin)  = child of R1   — sprite with pivot at CENTER
    // Foot       = child of R2   — empty transform at the tip
    //
    // The ACTUAL joint positions are defined by HingeJoint2D anchors,
    // NOT by transform.position (which is the sprite center).

    [Header("Segments (auto-detected if null)")]
    public Transform thigh;
    public Transform shin;
    public Transform foot;

    [Header("IK Settings")]
    public float thighLength = 6.8f;
    public float shinLength  = 6.8f;
    [Tooltip("Solver knee bend direction (may be opposite of physical side)")]
    public bool  isLeftSide  = false;
    [Tooltip("Which physical side of the body this leg is on (for targeting)")]
    public bool  isPhysicallyRight = false;

    [Header("LimbSolver Mode")]
    public Transform ikTarget;


    [Header("Editor")]
    public bool editorPreview = false;

    // ── Runtime state ──────────────────────────────────────────
    [HideInInspector] public Vector2 currentFootPos;
    [HideInInspector] public Vector2 targetFootPos;
    [HideInInspector] public bool    isStepping = false;
    [HideInInspector] public float   stepProgress = 0f;
    [HideInInspector] public bool    isPlanted  = false;
    [HideInInspector] public float   plantedTimer = 0f;
    [HideInInspector] public Vector2 plantedNormal;

    private Vector2 stepStartPos;
    private Vector2 currentStepTarget;
    private Vector2 currentStepNormal;
    private Vector2 lerpedStepTarget;
    private Vector2 lerpedStepNormal;
    
    public  Transform plantedTransform;
    private Vector2 plantedLocalPos;
    private Rigidbody2D plantedRb;

    private SpiderControllerIK controller;
    private bool useLimbSolver;

    // ── Joint anchor data (read from HingeJoint2Ds at startup) ──
    // These are LOCAL-SPACE positions of the actual joints on each sprite.
    [Header("Debug: Anchor Data (auto-filled)")]
    public Vector2 shoulderOnThigh;    // where thigh connects to body (thigh local space)
    public Vector2 elbowOnThigh;       // where shin connects to thigh (thigh local space)
    public Vector2 elbowOnShin;        // where shin connects to thigh (shin local space)
    public Vector2 bodyAnchor;         // where thigh connects on the body (body local space)
    public Transform bodyTransform;    // the ACTUAL transform bodyAnchor is relative to
    public float thighLocalBoneAngle;
    public float shinLocalBoneAngle;

    // ───────────────────────────────────────────────────────────
    //  Initialisation
    // ───────────────────────────────────────────────────────────

    public void Initialize(SpiderControllerIK ctrl)
    {
        controller = ctrl;
        useLimbSolver = (ctrl.solverMode == SpiderControllerIK.SolverMode.LimbSolver);

        AutoDetectTransforms();
        CaptureJointData();

        if (ikTarget == null && foot != null)
            ikTarget = foot;

        DisablePhysics(thigh);
        DisablePhysics(shin);
        DisablePhysics(foot);

        if (useLimbSolver && ikTarget != null)
            ikTarget.SetParent(null);

        // Seed foot position
        currentFootPos = foot != null ? (Vector2)foot.position : (Vector2)transform.position;
        targetFootPos = currentFootPos;
        isPlanted = true; // Start planted
        plantedNormal = Vector2.up; // Default
        
        if (useLimbSolver && ikTarget != null)
            ikTarget.position = (Vector3)currentFootPos;
    }

    // ───────────────────────────────────────────────────────────
    //  Read joint anchors from (disabled) HingeJoint2Ds
    // ───────────────────────────────────────────────────────────

    void CaptureJointData()
    {
        // ── Thigh's hinge → body ──
        var thighHinge = thigh.GetComponent<HingeJoint2D>();
        if (thighHinge != null)
        {
            shoulderOnThigh = thighHinge.anchor;
            bodyAnchor = thighHinge.connectedAnchor;
            // Store the ACTUAL connected body's transform (might be a parent container, NOT the head)
            bodyTransform = thighHinge.connectedBody != null
                ? thighHinge.connectedBody.transform
                : controller.transform;
        }
        else
        {
            shoulderOnThigh = Vector2.zero;
            bodyTransform = controller.transform;
        }

        // ── Shin's hinge → thigh ──
        // The shin may have 2 hinges (one to thigh, one to foot).
        // The one connecting to thigh has connectedBody = thigh's rigidbody.
        var shinHinges = shin.GetComponents<HingeJoint2D>();
        HingeJoint2D shinToThighHinge = null;
        var thighRb = thigh.GetComponent<Rigidbody2D>();

        foreach (var h in shinHinges)
        {
            if (h.connectedBody == thighRb)
            {
                shinToThighHinge = h;
                break;
            }
        }
        // Fallback: just use the first hinge
        if (shinToThighHinge == null && shinHinges.Length > 0)
            shinToThighHinge = shinHinges[0];

        if (shinToThighHinge != null)
        {
            elbowOnShin = shinToThighHinge.anchor;              // where on the shin the elbow is
            elbowOnThigh = shinToThighHinge.connectedAnchor;    // where on the thigh the elbow is
        }
        else
        {
            // Fallback: use child position relative to parent
            elbowOnShin = Vector2.zero;
            elbowOnThigh = (Vector2)thigh.InverseTransformPoint(shin.position);
        }

        // ── Compute bone lengths (world-space, scale-safe) ──
        Vector3 worldShoulder = thigh.TransformPoint(shoulderOnThigh);
        Vector3 worldElbow = thigh.TransformPoint(elbowOnThigh);
        thighLength = Vector3.Distance(worldShoulder, worldElbow);

        if (foot != null)
        {
            Vector3 worldElbow2 = shin.TransformPoint(elbowOnShin);
            shinLength = Vector3.Distance(worldElbow2, foot.position);
        }

        // ── Compute local bone angles ──
        // "Which direction does the bone point when the transform rotation is 0?"
        Vector2 thighBoneLocal = elbowOnThigh - shoulderOnThigh;
        thighLocalBoneAngle = Mathf.Atan2(thighBoneLocal.y, thighBoneLocal.x) * Mathf.Rad2Deg;

        Vector2 footOnShinLocal = (Vector2)shin.InverseTransformPoint(foot != null ? foot.position : shin.position);
        Vector2 shinBoneLocal = footOnShinLocal - elbowOnShin;
        shinLocalBoneAngle = Mathf.Atan2(shinBoneLocal.y, shinBoneLocal.x) * Mathf.Rad2Deg;
    }

    // ───────────────────────────────────────────────────────────
    //  Auto-detection
    // ───────────────────────────────────────────────────────────

    [ContextMenu("Auto-Detect Transforms")]
    public void AutoDetectTransforms()
    {
        if (thigh == null) thigh = transform;
        if (shin  == null && transform.childCount > 0) shin = transform.GetChild(0);
        if (foot  == null && shin != null && shin.childCount > 0) foot = shin.GetChild(0);
    }

    void OnValidate()
    {
        if (!Application.isPlaying)
        {
            if (thigh == null || shin == null)
                AutoDetectTransforms();

            if (editorPreview && thigh != null && shin != null)
            {
                CaptureJointData();
                if (foot != null)
                    currentFootPos = foot.position;
                SolveAnalytical();
            }
        }
    }

    [ContextMenu("Preview: Wide Stance")]
    void PreviewWideStance()
    {
        AutoDetectTransforms();
        CaptureJointData();
        float dir = isLeftSide ? -1f : 1f;
        Vector2 shoulder = thigh.TransformPoint(shoulderOnThigh);
        if (foot != null)
        {
            foot.position = shoulder
                          + Vector2.right * dir * (thighLength + shinLength) * 0.6f
                          + Vector2.down * (thighLength * 0.5f);
            currentFootPos = foot.position;
        }
        SolveAnalytical();
    }

    // ───────────────────────────────────────────────────────────
    //  Physics helpers
    // ───────────────────────────────────────────────────────────

    void DisablePhysics(Transform t)
    {
        if (t == null) return;
        var rb = t.GetComponent<Rigidbody2D>();
        if (rb != null)
        {
            rb.bodyType = RigidbodyType2D.Dynamic;
            rb.gravityScale = 0f;      // IK controls position, not gravity
            rb.mass = 0.1f;            // lightweight so physics doesn't fight IK
            rb.linearDamping = 10f;        // heavy damping prevents drift
        }
        foreach (var j in t.GetComponents<HingeJoint2D>())
            j.enabled = false;
    }

    // ───────────────────────────────────────────────────────────
    //  Solve  (called by controller AFTER body has moved)
    // ───────────────────────────────────────────────────────────

    public void Solve()
    {
        if (useLimbSolver)
        {
            if (ikTarget != null)
                ikTarget.position = (Vector3)currentFootPos;
        }
        else
        {
            SolveAnalytical();
        }
    }

    // ───────────────────────────────────────────────────────────
    //  Analytical Solver  (anchor-aware, sprite-center-safe)
    // ───────────────────────────────────────────────────────────

    void SolveAnalytical()
    {
        if (thigh == null || shin == null) return;

        // ── 1. Find the shoulder in world space ──
        // Use the ACTUAL connected body transform, not the controller
        Vector2 shoulderWorld;
        if (bodyTransform != null)
            shoulderWorld = bodyTransform.TransformPoint(bodyAnchor);
        else
            shoulderWorld = thigh.TransformPoint(shoulderOnThigh);

        // ── 2. Solve IK ──
        Vector2 toTarget = currentFootPos - shoulderWorld;
        Vector2 angles = SpiderIKUtility.Solve2BIK(toTarget, thighLength, shinLength, isLeftSide);

        // ── 3. Rotate thigh, then snap its shoulder anchor to shoulderWorld ──
        float thighSpriteAngle = angles.x - thighLocalBoneAngle;
        thigh.rotation = Quaternion.Euler(0, 0, thighSpriteAngle);
        // TransformPoint accounts for position + rotation + SCALE
        Vector2 currentShoulder = thigh.TransformPoint(shoulderOnThigh);
        thigh.position += (Vector3)(shoulderWorld - currentShoulder);

        // ── 4. Compute elbow position ──
        float thighBoneRad = angles.x * Mathf.Deg2Rad;
        Vector2 elbowWorld = shoulderWorld + new Vector2(Mathf.Cos(thighBoneRad), Mathf.Sin(thighBoneRad)) * thighLength;

        // ── 5. Rotate shin, then snap its elbow anchor to elbowWorld ──
        float shinBoneAngle = angles.x - angles.y;  // SUBTRACT — knee bends away from thigh direction
        float shinSpriteAngle = shinBoneAngle - shinLocalBoneAngle;
        shin.rotation = Quaternion.Euler(0, 0, shinSpriteAngle);
        Vector2 currentElbow = shin.TransformPoint(elbowOnShin);
        shin.position += (Vector3)(elbowWorld - currentElbow);
    }

    // ───────────────────────────────────────────────────────────
    //  Stepping
    // ───────────────────────────────────────────────────────────

    public void StepTo(Vector2 worldTarget, Vector2 normal, Collider2D col = null)
    {
        if (isStepping) return;
        
        // Physical Kick: Apply a force to the object we step on for a realistic look
        if (col != null)
        {
            var targetRb = col.GetComponentInParent<Rigidbody2D>();
            if (targetRb != null && targetRb != controller.headRb)
            {
                // Apply a significant push at the step point
                targetRb.AddForceAtPosition(-normal * (controller.headRb.mass * 1.5f), worldTarget, ForceMode2D.Impulse);
            }
        }

        // Web Pulse Illumination: Trigger pulse when targeting a web
        if (col != null)
        {
            var web = col.GetComponent<WebSegment>();
            if (web != null) web.TriggerPulse();
        }

        currentStepTarget = worldTarget;
        currentStepNormal = normal;
        plantedNormal = normal;
        lerpedStepTarget = worldTarget;
        lerpedStepNormal = normal;
        
        // Clear old plant data
        plantedTransform = col != null ? col.transform : null;
        if (plantedTransform != null)
        {
            plantedRb = col.GetComponentInParent<Rigidbody2D>();
            plantedLocalPos = plantedTransform.InverseTransformPoint(worldTarget);
        }
        else
        {
            plantedRb = null;
        }

        StartCoroutine(StepCoroutine());
    }

    public void UpdateStepTarget(Vector2 worldTarget, Vector2 normal, Collider2D col = null)
    {
        if (!isStepping) return;
        currentStepTarget = worldTarget;
        currentStepNormal = normal;

        // CRITICAL: Update the tracking data so SyncToSurface doesn't overwrite this reactive adjustment
        if (col != null)
        {
            plantedTransform = col.transform;
            plantedRb = col.GetComponentInParent<Rigidbody2D>();
            plantedLocalPos = plantedTransform.InverseTransformPoint(worldTarget);
        }
        else if (plantedTransform != null)
        {
            // If the surface didn't change, just update the local anchor point on the current surface
            plantedLocalPos = plantedTransform.InverseTransformPoint(worldTarget);
        }
    }

    IEnumerator StepCoroutine()
    {
        isStepping = true;
        isPlanted  = false;
        stepStartPos = currentFootPos;

        float t = 0f;
        while (t < 1f)
        {
            // If the target object is moving (like a sagging web), update our world target to follow it
            if (plantedTransform != null)
            {
                currentStepTarget = plantedTransform.TransformPoint(plantedLocalPos);
            }

            // Smoothly transition the target/normal in mid-air
            lerpedStepTarget = Vector2.Lerp(lerpedStepTarget, currentStepTarget, Time.deltaTime * 15f);
            lerpedStepNormal = Vector2.Lerp(lerpedStepNormal, currentStepNormal, Time.deltaTime * 15f);

            // Distance-Based Progress
            float currentTotalDist = Vector2.Distance(stepStartPos, lerpedStepTarget);
            if (currentTotalDist > 0.01f)
                t += (controller.stepSpeed * Time.deltaTime) / currentTotalDist;
            else
                t = 1f;

            if (t > 1f) t = 1f;
            stepProgress = t;

            Vector2 mid     = (stepStartPos + lerpedStepTarget) * 0.5f;
            Vector2 control = mid + lerpedStepNormal * controller.stepHeight;
            
            currentFootPos = Bezier(stepStartPos, control, lerpedStepTarget, t);
            yield return null;
        }

        currentFootPos = currentStepTarget;
        targetFootPos  = currentStepTarget;
        isStepping = false;
        stepProgress = 0f;
        isPlanted  = true;
        plantedTimer = 0f;

        // Final snap to target object if it moved during the step
        if (plantedTransform != null)
        {
            plantedLocalPos = plantedTransform.InverseTransformPoint(currentStepTarget);
            currentFootPos = plantedTransform.TransformPoint(plantedLocalPos);

            // Web Pulse Illumination: Trigger pulse when stepping on webs
            var web = plantedTransform.GetComponent<WebSegment>();
            if (web != null)
            {
                web.TriggerPulse();
            }
        }
    }

    static Vector2 Bezier(Vector2 a, Vector2 b, Vector2 c, float t)
    {
        float u = 1f - t;
        return u * u * a + 2f * u * t * b + t * t * c;
    }

    // ───────────────────────────────────────────────────────────
    //  Queries
    // ───────────────────────────────────────────────────────────

    public float GetExtension()
    {
        Vector2 shoulder = bodyTransform != null
            ? (Vector2)bodyTransform.TransformPoint(bodyAnchor)
            : (Vector2)thigh.TransformPoint(shoulderOnThigh);
        return Vector2.Distance(shoulder, currentFootPos);
    }

    public Vector2 GetKneePosition()
    {
        if (thigh == null) return transform.position;
        return thigh.TransformPoint(elbowOnThigh);
    }

    public void SyncToSurface()
    {
        if (plantedTransform != null)
        {
            // Update the world position based on the transform it's attached to
            Vector2 newWorldPos = plantedTransform.TransformPoint(plantedLocalPos);
            
            if (isStepping)
            {
                // If we are stepping, we update the target so the leg follows the moving surface mid-air
                currentStepTarget = newWorldPos;
            }
            else if (isPlanted)
            {
                // If we are planted, the foot stays locked to the surface
                currentFootPos = newWorldPos;
                targetFootPos  = newWorldPos;
            }
        }
    }

    void FixedUpdate()
    {
        SyncToSurface();
    }

    // ───────────────────────────────────────────────────────────
    //  Debug Gizmos
    // ───────────────────────────────────────────────────────────

    void OnDrawGizmos()
    {
        if (controller == null || !controller.showDebug) return;
        if (thigh == null || shin == null) return;

        // Show the TRUE joint positions (from anchors, not sprite centers)
        Transform bt = bodyTransform != null ? bodyTransform : controller.transform;
        Vector2 shoulder = bt.TransformPoint(bodyAnchor);
        Vector2 elbow    = thigh.TransformPoint(elbowOnThigh);
        Vector2 tip      = foot != null ? (Vector2)foot.position : (Vector2)shin.position;

        // Green: true bone chain (joint to joint)
        Gizmos.color = Color.green;
        Gizmos.DrawLine(shoulder, elbow);
        Gizmos.DrawLine(elbow, tip);

        // White circles at actual joints
        Gizmos.color = Color.white;
        Gizmos.DrawWireSphere(shoulder, 0.3f); // shoulder
        Gizmos.DrawWireSphere(elbow, 0.2f);    // elbow

        // Yellow: foot tip
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(tip, 0.25f);

        // Cyan: IK target
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(currentFootPos, 0.2f);
        Gizmos.DrawLine(tip, currentFootPos);

        // Red dots at sprite centers (to show they're NOT the joints)
        Gizmos.color = new Color(1, 0, 0, 0.4f);
        Gizmos.DrawSphere(thigh.position, 0.15f);
        Gizmos.DrawSphere(shin.position, 0.15f);
    }
}
