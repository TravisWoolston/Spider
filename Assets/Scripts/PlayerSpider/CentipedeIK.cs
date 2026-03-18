using UnityEngine;
using System.Collections.Generic;

public class CentipedeIK : MonoBehaviour
{
    // ── Enums ──────────────────────────────────────────────────
    public enum GaitType   { Urgency, Alternate }
    public enum SolverMode { Analytical, LimbSolver }

    // ── Inspector ──────────────────────────────────────────────
    [Header("IK Solver")]
    public SolverMode solverMode = SolverMode.LimbSolver;
    public bool isAI = false;

    [Header("Centipede Generation")]
    public GameObject bodySegmentPrefab;
    public int numberOfSegmentsToGenerate = 5;

    [Header("Centipede Logic")]
    public Transform[] bodySegments;
    public float segmentRotationSpeed = 15f;
    public bool usePhysicsSegments = true; // Switch to physics-based hinges
    public float segmentGravityScale = 0.2f;
    public float segmentMass = 0.5f;

    [Header("Legs")]
    public CentipedeLegIK[] legs;
    public GaitType activeGait = GaitType.Urgency;
    public float legExtensionMax = 10f;
    public float legExtensionMin = 0.2f;
    public float footSpacing = 2f;
    public float minStride = 1f;
    public float headSafeArea  = 0.5f;

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

    [Header("Art Alignment")]
    [Tooltip("Correction for the head sprite. 0=Right, 90=Up, 180=Left. Default 90 (Pincers are Up).")]
    public float headForwardOffset = 90f;
    [Tooltip("Correction for body segments. Default 180 (Spine flows from Left to Right).")]
    public float segmentForwardOffset = 180f;

    public float stepCooldown    = 0.3f;
    public float stepSpeed       = 20f;
    public float stepHeight      = 2f;
    public LayerMask attachableLayers;

    [Header("Raycasting")]
    public int   rayCount      = 100;
    public int   bodyRayCount  = 20; // 360 sweep is overkill for trail segments
    public float sightDistance  = 15f;

    [Header("Web Physics")]
    public float spiderWeightMult = 1.0f; // Factor of spider's mass applied to web segments
    public float webWeightScale => spiderWeightMult; // Deprecated shim

    [Header("Gait Rhythm")]
    public float metachronalSpeed = 8f;   // How fast the ripple travels
    public float metachronalStrength = 1.5f; // How much it influences urgency

    [Header("Reach & Stability")]
    [Tooltip("Percent of legs (0-1) needed to maintain full lift (default 0.2)")]
    public float fallThreshold = 0.2f;
    [Tooltip("How much 'Inertia' the centipede retains while falling (0-1, default 0.2)")]
    public float minControlRetention = 0.2f;
    [Tooltip("Strength of the active nose-lift (default 2500)")]
    public float liftPotency = 2500f;
    [Tooltip("Strength of the body supporting arch (default 1500)")]
    public float muscleArchForce = 1500f;
    [Tooltip("Strength of body contraction towards head (default 500)")]
    public float contractionForce = 500f;

    [Tooltip("Strength of the muscle-system when airborne (0-1, default 1.0)")]
    public float airborneMuscleStiffness = 1.0f;
    [Tooltip("Maximum bend allowed between any two body segments (degrees, default 40)")]
    public float maxSegmentBend = 40f;

    [Header("Debug")]
    public bool showDebug = true;

    // ── Runtime state ──────────────────────────────────────────
    private float targetBodyHeight = 0f;
    [HideInInspector] public Vector2 inputDirection;
    private Vector2 smoothedInput;
    private Vector2 steeredMovementDir;
    private float currentSteerHeading; // The world heading derived from steering
    private bool isReverseGear;        // Hysteresis state for forward/reverse bias
    private Vector2 currentSurfaceNormal = Vector2.up;
    private float masterSteerTarget;   // The un-clamped world goal from the player stick

    private List<AttachmentPoint> attachmentPoints = new();
    private Vector2[] restingFootOffsets;
    private int nextLegIndex;
    private int lastSteppedIndex = -1;
    private float metachronalPhase; // Decoupled phase for gait rhythm
    private float stickInfluence; // Smoothed blender for steering vs sprawl

    // Zero-allocation buffers for physics queries
    private RaycastHit2D[] hitBuffer = new RaycastHit2D[100];
    private Collider2D[] overlapBuffer = new Collider2D[2000]; // Can comfortably handle large webs

    // Cached head transform — all orientation/position goes through this
    private Transform headT;
    
    // Performance Caching
    private Vector2 headBackAnchor;
    private Vector2[] segmentFrontAnchors;
    private Vector2[] segmentBackAnchors;
    private Dictionary<Collider2D, int> strandCounts = new();

    // ───────────────────────────────────────────────────────────
    //  Lifecycle
    // ───────────────────────────────────────────────────────────

    void Start()
    {
        if (isAI)
        {
            // 1. Auto-discover Legs
            if (legs == null || legs.Length == 0)
                legs = GetComponentsInChildren<CentipedeLegIK>();

            // 2. Auto-discover Head/Body
            if (headRb == null) headRb = GetComponent<Rigidbody2D>();
            if (headRb == null) headRb = GetComponentInChildren<Rigidbody2D>();
        }

        // Initialize Body Height Persistence
        if (defaultBodyHeight == 0f) defaultBodyHeight = bodyHeight;
        targetBodyHeight = bodyHeight;

        // 3. Ignore Web Collision (Physical snagging)
        // This ensures the body segments don't 'bump' into the webs, but rays/feet can still find them.
        int webLayer = LayerMask.NameToLayer("Web");
        if (webLayer != -1)
        {
            Physics2D.IgnoreLayerCollision(gameObject.layer, webLayer, true);
        }

        // Basic initialization required for both Player and AI
        headT = headRb != null ? headRb.transform : transform;

        if (bodySegments != null)
        {
            Transform prev = headT;
            Rigidbody2D prevRb = headRb;

            foreach (Transform seg in bodySegments)
            {
                if (seg == null) continue;
                
                Rigidbody2D rb = seg.GetComponent<Rigidbody2D>();
                HingeJoint2D[] joints = seg.GetComponents<HingeJoint2D>();

                if (usePhysicsSegments)
                {
                    if (rb != null)
                    {
                        rb.bodyType = RigidbodyType2D.Dynamic;
                        rb.gravityScale = segmentGravityScale;
                        rb.mass = segmentMass;
                        rb.linearDamping = 2f;
                        rb.angularDamping = 2f;
                    }

                    for (int j = 0; j < joints.Length; j++)
                    {
                        if (j == 0)
                        {
                            joints[j].enabled = true;
                            joints[j].connectedBody = prevRb;
                            joints[j].autoConfigureConnectedAnchor = false;
                            joints[j].connectedAnchor = GetBackAnchorLocal(prev);
                        }
                        else
                        {
                            joints[j].enabled = false; // Disable reference/trailing joints
                        }
                    }
                }
                else
                {
                    if (rb != null) rb.bodyType = RigidbodyType2D.Kinematic;
                    foreach (var joint in joints) joint.enabled = false;
                }
                prev = seg;
                prevRb = rb;
            }
        }

        // Optional: Enable head hinge for angle limits if the user has one configured
        if (usePhysicsSegments)
        {
            var headJoints = headT.GetComponents<HingeJoint2D>();
            if (headJoints.Length > 0)
            {
                headJoints[0].enabled = true;
                // If it's the first joint in the whole chain, it might be an "anchor" joint
                // or the user might have wired it to the first segment. 
            }
        }
        else
        {
            foreach (var joint in headT.GetComponents<HingeJoint2D>())
                joint.enabled = false;
        }

        if (headRb != null)
        {
            headRb.bodyType = RigidbodyType2D.Dynamic;
            headRb.gravityScale = 1f;
            headRb.freezeRotation = true;
        }

        // Force include Default and Web layers if they aren't set
        attachableLayers |= (1 << LayerMask.NameToLayer("Default"));
        attachableLayers |= (1 << LayerMask.NameToLayer("Web"));

        foreach (var leg in legs)
            leg.Initialize(this);

        PlantAllFeet();
        
        // Cache anchors to avoid GetComponents in the update loop
        headBackAnchor = GetBackAnchorLocal(headT);
        if (bodySegments != null)
        {
            segmentFrontAnchors = new Vector2[bodySegments.Length];
            segmentBackAnchors = new Vector2[bodySegments.Length];
            for (int i = 0; i < bodySegments.Length; i++)
            {
                segmentFrontAnchors[i] = GetFrontAnchorLocal(bodySegments[i]);
                segmentBackAnchors[i] = GetBackAnchorLocal(bodySegments[i]);
            }
        }

        CaptureRestingPose();

        foreach (var leg in legs)
            leg.Solve();
    }

    void PlantAllFeet()
    {
        if (legs == null) return;
        for (int i = 0; i < legs.Length; i++)
        {
            var leg = legs[i];
            if (leg == null) continue;
            float reach = (leg.thighLength + leg.shinLength) * 2.0f; 
            Transform segment = leg.bodyTransform != null ? leg.bodyTransform : headT;
            Vector2 segPos = segment.position;

            // Cast in a sphere or multiple directions to ensure we find a floor
            RaycastHit2D hit = Physics2D.CircleCast(segPos, 0.5f, -segment.up, reach, attachableLayers);
            if (hit.collider == null)
                hit = Physics2D.Raycast(segPos, Vector2.down, reach, attachableLayers);

            if (hit.collider == null)
            {
                // Fallback looking slightly ahead/behind if straight down fails under the segment
                hit = Physics2D.Raycast(segPos + (Vector2)segment.right * reach * 0.5f,
                                        -segment.up, reach, attachableLayers);
            }
            if (hit.collider == null)
            {
                hit = Physics2D.Raycast(segPos - (Vector2)segment.right * reach * 0.5f,
                                        -segment.up, reach, attachableLayers);
            }

            if (hit.collider != null)
            {
                // Inline ForcePlant: CentipedeLegIK doesn't have this method
                leg.currentFootPos = hit.point;
                leg.targetFootPos  = hit.point;
                leg.isPlanted = true;
                leg.plantedNormal = hit.normal;
                leg.plantedTransform = hit.collider.transform;
                // Store local offset so SyncToSurface works on the first frame
                leg.plantedLocalPos = hit.collider.transform.InverseTransformPoint(hit.point);
            }
        }
    }

    void Update()
    {
        ReadInput();
        if (inputDirection.sqrMagnitude > 0.01f)
            smoothedInput = Vector2.MoveTowards(smoothedInput, inputDirection, Time.deltaTime * 5f);
        else
            smoothedInput = Vector2.MoveTowards(smoothedInput, Vector2.zero, Time.deltaTime * 8f);

        foreach (var leg in legs)
            if (leg.isPlanted) leg.plantedTimer += Time.deltaTime;

        // 3. THROTTLED Reactive Mid-Step Adjustment
        foreach (var leg in legs)
        {
            if (leg.isStepping && leg.stepProgress > 0.2f && leg.stepProgress < 0.3f) 
            {
                AttachmentPoint best = GetBestPoint(leg);
                if (best.isValid)
                    leg.UpdateStepTarget(best.point, best.normal, best.collider);
            }
        }
    }

    void UpdateSteering()
    {
        if (smoothedInput.sqrMagnitude < 0.01f)
        {
            steeredMovementDir = Vector2.zero;
            return;
        }

        // --- WORLD SPACE MOVEMENT ---
        // The player always moves in the direction they push. No body-relative clamping on movement.
        steeredMovementDir = smoothedInput;

        // --- VISUAL HEAD STEERING (Car-style, clamped to hinge limits) ---
        float inputAngle = Mathf.Atan2(smoothedInput.y, smoothedInput.x) * Mathf.Rad2Deg;
        Transform baseT = (bodySegments != null && bodySegments.Length > 0) ? bodySegments[0] : headT;
        float baseAngle = baseT.eulerAngles.z + segmentForwardOffset;
        float rel = Mathf.DeltaAngle(baseAngle, inputAngle);

        var hinge = headT.GetComponent<HingeJoint2D>();
        float min = (hinge != null && hinge.useLimits) ? hinge.limits.min : -120f;
        float max = (hinge != null && hinge.useLimits) ? hinge.limits.max : 120f;

        // --- FORWARD BIASED HYSTERESIS ---
        if (isReverseGear)
        {
            if (Mathf.Abs(rel) < 85f) isReverseGear = false;
        }
        else
        {
            if (Mathf.Abs(rel) > 125f) isReverseGear = true;
        }

        if (isReverseGear)
        {
            float revRel = Mathf.DeltaAngle(baseAngle + 180f, inputAngle);
            float clampedRev = Mathf.Clamp(revRel, min, max);
            currentSteerHeading = baseAngle - clampedRev; 
        }
        else
        {
            float clampedRel = Mathf.Clamp(rel, min, max);
            currentSteerHeading = baseAngle + clampedRel;
        }
    }

    void FixedUpdate()
    {
        UpdateSteering();
        ProcessGait();
        PerformRaycasts();

        // Update segments before body pose so the legs have accurate local origins
        if (!usePhysicsSegments)
            UpdateSegments();

        // Body pose solves legs, not segment physics (physics engine handles segments natively)
        SolveBodyPose();
    }

    private Vector2 GetFrontAnchorLocal(Transform t)
    {
        HingeJoint2D[] joints = t.GetComponents<HingeJoint2D>();
        if (joints.Length > 0) return joints[0].anchor;
        return Vector2.zero;
    }

    private Vector2 GetBackAnchorLocal(Transform t)
    {
        HingeJoint2D[] joints = t.GetComponents<HingeJoint2D>();
        if (joints.Length > 1) return joints[1].anchor;
        if (joints.Length == 1) return joints[0].anchor;
        return Vector2.zero;
    }

    [ContextMenu("Generate Centipede Body")]
    public void GenerateCentipedeBody()
    {
        if (bodySegmentPrefab == null)
        {
            Debug.LogError("No Body Segment Prefab assigned! Cannot generate Centipede.");
            return;
        }

        // Destroy existing segments if any
        if (bodySegments != null && bodySegments.Length > 0)
        {
            foreach (var seg in bodySegments)
            {
                if (seg != null)
                {
#if UNITY_EDITOR
                    UnityEditor.Undo.DestroyObjectImmediate(seg.gameObject);
#else
                    DestroyImmediate(seg.gameObject);
#endif
                }
            }
        }

        bodySegments = new Transform[numberOfSegmentsToGenerate];
        
        List<CentipedeLegIK> newLegs = new List<CentipedeLegIK>();

        // We assume we are running this on the Head object or its parent container.
        Transform headTransform = headRb != null ? headRb.transform : transform;

        Transform prevTransform = headTransform;
        
        for (int i = 0; i < numberOfSegmentsToGenerate; i++)
        {
#if UNITY_EDITOR
            GameObject newSegment = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(bodySegmentPrefab, transform);
            UnityEditor.Undo.RegisterCreatedObjectUndo(newSegment, "Generate Centipede Segment");
#else
            GameObject newSegment = Instantiate(bodySegmentPrefab, transform);
#endif
            newSegment.name = "Centipede Segment " + (i + 1);
            
            // Rotate correctly to point from target to whatever local space is logical
            newSegment.transform.rotation = prevTransform.rotation;
            
            // Snap to previous anchor
            Vector2 frontLocal = GetFrontAnchorLocal(newSegment.transform);
            Vector2 backLocalPrev = GetBackAnchorLocal(prevTransform);
            Vector2 targetWorld = prevTransform.TransformPoint(backLocalPrev);
            Vector2 rotatedFront = newSegment.transform.TransformVector(frontLocal);
            newSegment.transform.position = targetWorld - rotatedFront;

            // Wire up the HingeJoint2D logic
            HingeJoint2D[] joints = newSegment.GetComponents<HingeJoint2D>();
            if (joints.Length > 0)
            {
                HingeJoint2D frontJoint = joints[0];
                frontJoint.connectedBody = prevTransform.GetComponent<Rigidbody2D>();
                frontJoint.anchor = frontLocal;
                frontJoint.connectedAnchor = backLocalPrev;
                frontJoint.autoConfigureConnectedAnchor = false;
            }
            
            bodySegments[i] = newSegment.transform;
            prevTransform = newSegment.transform;

            // Collect any legs attached to this segment
            CentipedeLegIK[] segmentLegs = newSegment.GetComponentsInChildren<CentipedeLegIK>();
            newLegs.AddRange(segmentLegs);
            
            // Make sure the legs on the new segments know their body part so they act correctly
            foreach(var leg in segmentLegs){
                leg.bodyTransform = newSegment.transform;
            }
        }

        // Collect any head legs first
        List<CentipedeLegIK> allLegs = new List<CentipedeLegIK>();
        allLegs.AddRange(headTransform.GetComponentsInChildren<CentipedeLegIK>());
        allLegs.AddRange(newLegs);

        legs = allLegs.ToArray();

        // Autodetect Leg properties immediately
        foreach (var leg in legs)
        {
            leg.AutoDetectTransforms();
        }
        
    }

    void UpdateSegments()
    {
        if (bodySegments == null || bodySegments.Length == 0) return;

        Transform prev = headT;
        for (int i = 0; i < bodySegments.Length; i++)
        {
            Transform current = bodySegments[i];
            if (current == null) continue;

            Vector2 targetPos = prev.TransformPoint(i == 0 ? headBackAnchor : segmentBackAnchors[i-1]);
            
            // The unrotated vector representing the physical connection axis of this body segment
            Vector2 localBone = segmentFrontAnchors[i] - segmentBackAnchors[i];
            // The world space vector stretching from this segment's dragged tail to its target
            Vector2 currentBackWorld = current.TransformPoint(segmentBackAnchors[i]);
            Vector2 worldBone = targetPos - currentBackWorld;

            if (worldBone.sqrMagnitude > 0.001f && localBone.sqrMagnitude > 0.001f)
            {
                float targetAngle = Mathf.Atan2(worldBone.y, worldBone.x) * Mathf.Rad2Deg;
                float localAngle = Mathf.Atan2(localBone.y, localBone.x) * Mathf.Rad2Deg;

                Quaternion targetRot = Quaternion.Euler(0, 0, targetAngle - localAngle);
                current.rotation = Quaternion.Lerp(current.rotation, targetRot, segmentRotationSpeed * Time.fixedDeltaTime);
            }

            // Snap the segment's position so its front anchor binds exactly to the previous's back anchor
            Vector2 worldFrontAnchor = current.TransformVector(segmentFrontAnchors[i]);
            current.position = targetPos - worldFrontAnchor;

            prev = current;
        }
    }

    void LateUpdate()
    {
        // UpdateSegments moved to FixedUpdate
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

        // 2. Automated dodging
        if (smoothedInput.sqrMagnitude > 0.1f)
        {
            // Use the average surface normal for orientation-agnostic dodging
            float dodgeBias = Vector2.Dot(smoothedInput, currentSurfaceNormal);
            
            // REMOVED 'Auto Reach' hunker logic to allow 'airplane' flight into space.
            // Previously, the head would drop if no surfaces were detected ahead.
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
        attachmentPoints.Clear();
        int webLayer = LayerMask.NameToLayer("Web");

        List<Vector2> raycastOrigins = new List<Vector2>();
        raycastOrigins.Add(headRb != null ? headRb.position : (Vector2)headT.position);
        if (bodySegments != null)
        {
            foreach (Transform seg in bodySegments)
            {
                if (seg != null) raycastOrigins.Add(seg.position);
            }
        }

        foreach (Vector2 origin in raycastOrigins)
        {
            bool isHead = (origin == (headRb != null ? headRb.position : (Vector2)headT.position));
            int currentRayCount = isHead ? rayCount : bodyRayCount;
            float step = 360f / currentRayCount;

            for (int i = 0; i < currentRayCount; i++)
            {
                float rad = i * step * Mathf.Deg2Rad;
                Vector2 dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));

                // Use RaycastAll so we can "See through" multiple strands of web
                int hitCount = Physics2D.RaycastNonAlloc(origin, dir, hitBuffer, sightDistance, attachableLayers);
                
                if (hitCount > 0)
                {
                    // Manual insertion sort... (rest of logic)
                    // (I will simplify this block in the actual replacement)
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
                        if (hit.collider.gameObject.layer == webLayer && dist < 0.2f)
                            continue;

                        attachmentPoints.Add(new AttachmentPoint(hit.point, hit.normal, hit.collider));
                        if (hit.collider.gameObject.layer != webLayer) break;
                    }
                }
            }

            // --- Reduced Volume-based detection for body segments ---
            int overlapCount = Physics2D.OverlapCircleNonAlloc(origin, isHead ? sightDistance : sightDistance * 0.5f, overlapBuffer, LayerMask.GetMask("Web"));
            for (int i = 0; i < overlapCount; i++)
            {
                Collider2D col = overlapBuffer[i];
                Vector2 pL = col.ClosestPoint(origin);
                attachmentPoints.Add(new AttachmentPoint(pL, (origin - pL).normalized, col));
            }
        }

        // --- Knee-based Raycasting for local obstacle/cliff detection ---
        foreach (var leg in legs)
        {
            Vector2 kneeOrigin = leg.GetKneePosition();
            float kneeRayCount = 30;
            float kneeStep = 360f / kneeRayCount;
            for (int i = 0; i < kneeRayCount; i++)
            {
                float rad = i * kneeStep * Mathf.Deg2Rad;
                Vector2 dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
                int hitCount = Physics2D.RaycastNonAlloc(kneeOrigin, dir, hitBuffer, sightDistance * 0.5f, attachableLayers);
                
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
                        
                        // Allow webs even if they are very close to the head (overlap prevention)
                        bool isWeb = hit.collider.gameObject.layer == webLayer;

                        var ap = new AttachmentPoint(hit.point, hit.normal, hit.collider);
                        attachmentPoints.Add(ap);
                        
                        // Store in global manager for AI wandering
                        GlobalAttachmentManager.StoreAttachmentPoint(hit.point, hit.normal, isWeb ? hit.collider.transform : null);
                    }
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
        float totalHeight = 0;
        int count = 0;

        for (int i = 0; i < legs.Length; i++)
        {
            Transform segment = legs[i].bodyTransform != null ? legs[i].bodyTransform : headT;
            Vector2 local = segment.InverseTransformPoint(legs[i].currentFootPos);
            restingFootOffsets[i] = local; 

            totalHeight += local.y;
            count++;
        }
        
        if (count > 0)
        {
            float avgHeight = totalHeight / count;
            // Force a sane centipede height. 8.0 is way too high.
            bodyHeight = Mathf.Clamp(-avgHeight, -1.0f, 1.5f); 
            defaultBodyHeight = bodyHeight;
        }
        targetBodyHeight = bodyHeight;
    }

    // ───────────────────────────────────────────────────────────
    //  Gaits
    // ───────────────────────────────────────────────────────────

    void ProcessGait()
    {
        // 1. PHASE TRACKING: Decouple rhythm from world time.
        // Accumulate phase based on velocity + movement intent.
        float speedScale = headRb != null ? headRb.linearVelocity.magnitude : 0f;
        float intentScale = smoothedInput.sqrMagnitude > 0.1f ? 1.0f : 0.0f;
        metachronalPhase += (speedScale * 1.25f + intentScale * 2.0f) * metachronalSpeed * Time.fixedDeltaTime;

        // 2. RECOVERY: Detached/Tucked legs MUST seek a surface
        // STAGGERED RECOVERY: We only allow a leg to replant if its 'wave' is in the favorable phase.
        // This ensures the centipede lands in the correct order instead of all-at-once.
        foreach (var leg in legs)
        {
            if (leg.isStepping || leg.isPlanted) continue;
            
            int idx = System.Array.IndexOf(legs, leg);
            float waveOffset = (float)idx * 0.5f; 
            float wave = Mathf.Sin(metachronalPhase - waveOffset);

            // Staggered replant: must be in the leading half of its specific wave (relaxed threshold)
            if (wave > -0.3f && HasValidTarget(leg, true)) 
            {
                StepLeg(leg, true);
                lastSteppedIndex = idx;
            }
        }

        if (activeGait == GaitType.Urgency)  UrgencyGait();
        else                                  AlternateGait();
    }

    void UrgencyGait()
    {
        // 1. Emergency Check: Over-extended legs MUST step regardless of gait constraints
        foreach (var leg in legs)
        {
            if (leg.isStepping) continue;
            
            // EMERGENCY: Stretched past limit (detached legs handled in ProcessGait)
            if (leg.isPlanted && leg.GetExtension() > legExtensionMax)
            {
                if (HasValidTarget(leg, false)) 
                {
                    StepLeg(leg, false);
                    lastSteppedIndex = System.Array.IndexOf(legs, leg);
                    return; 
                }
            }
        }

        if (smoothedInput.sqrMagnitude < 0.001f) return;

        CentipedeLegIK best = null;
        float maxUrg = 1f;

        foreach (var leg in legs)
        {
            if (!leg.isPlanted || leg.isStepping) continue;

            float u = Urgency(leg);
            if (u > maxUrg) 
            { 
                if (HasValidTarget(leg, false))
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
        if (smoothedInput.sqrMagnitude < 0.001f) return;
        
        var leg = legs[nextLegIndex];
        if (leg.isPlanted && !leg.isStepping && Urgency(leg) > 1f)
        {
            StepLeg(leg);
            nextLegIndex = (nextLegIndex + 1) % legs.Length;
        }
    }

    float Urgency(CentipedeLegIK leg)
    {
        int idx = System.Array.IndexOf(legs, leg);
        if (idx < 0 || restingFootOffsets == null) return 0f;

        // OPPORTUNISTIC: If not planted, urgency is always maxed until we find a home
        if (!leg.isPlanted && !leg.isStepping) return 500f; 

        Transform segment = leg.bodyTransform != null ? leg.bodyTransform : headT;

        // Ideal position is biased by the steered direction
        float maxReach = leg.thighLength + leg.shinLength;
        Vector2 idealPos = (Vector2)segment.TransformPoint(restingFootOffsets[idx])
                         + steeredMovementDir * maxReach * 0.45f;

        float distFromIdeal = Vector2.Distance(leg.currentFootPos, idealPos);
        float idealUrgency = distFromIdeal / (maxReach * 0.7f);

        float ext = leg.GetExtension() / legExtensionMax;
        float minExt = leg.GetExtension() / legExtensionMin;
        float extensionUrgency = (ext > 0.9f) ? (ext - 0.9f) * 10f : 0f;
        if (minExt < 1.1f) extensionUrgency += (1.1f - minExt) * 2f;

        // 4. Metachronal Wave Rhythm
        // Creates the "ripple" effect by biasing urgency spatially over time
        float waveOffset = (float)idx * 0.5f; // Spacing of the ripple
        float wave = Mathf.Sin(metachronalPhase - waveOffset);
        float rhythmUrgency = (wave + 1f) * 0.5f * metachronalStrength * (0.2f + (steeredMovementDir.magnitude * 0.8f));
        
        if (lastSteppedIndex != -1)
        {
            rhythmUrgency *= (legs[lastSteppedIndex] == leg) ? 0.2f : 1.0f;
        }

        // Only have high positional urgency if we can actually take a significant stride
        float positionalUrgency = (distFromIdeal < minStride || steeredMovementDir.sqrMagnitude < 0.01f) ? 0f : idealUrgency;

        // 6. Proximity Urgency (Avoid legs bunching up)
        float proximityUrgency = 0f;
        int myIdx = System.Array.IndexOf(legs, leg);
        int range = 3;
        int start = Mathf.Max(0, myIdx - range);
        int end = Mathf.Min(legs.Length, myIdx + range + 1);

        for (int i = start; i < end; i++)
        {
            var other = legs[i];
            if (other == leg || !other.isPlanted) continue;
            float d = Vector2.Distance(leg.currentFootPos, other.currentFootPos);
            if (d < footSpacing * 2.5f)
                proximityUrgency += 2.0f * (1.1f - (d / (footSpacing * 2.5f)));
        }

        return Mathf.Max(positionalUrgency, extensionUrgency) + proximityUrgency * 0.4f + rhythmUrgency;
    }

    // ───────────────────────────────────────────────────────────
    //  Stepping
    // ───────────────────────────────────────────────────────────

    void StepLeg(CentipedeLegIK leg, bool isRecovery = false)
    {
        AttachmentPoint best = GetBestPoint(leg, isRecovery);
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

    AttachmentPoint GetBestPoint(CentipedeLegIK leg, bool isRecovery = false)
    {
        int webLayer = LayerMask.NameToLayer("Web");
        int idx = System.Array.IndexOf(legs, leg);
        if (attachmentPoints.Count == 0) return new AttachmentPoint { isValid = false };

        Transform segment = leg.bodyTransform != null ? leg.bodyTransform : headT;
        Vector2 headPos = headRb != null ? headRb.position : (Vector2)headT.position;

        float maxReach = leg.thighLength + leg.shinLength;
        Vector2 idealPos = (Vector2)segment.TransformPoint(restingFootOffsets[idx])
                         + steeredMovementDir * maxReach * 0.45f;

        // If we are recovering (finding a surface from air), be much pickier!
        float effectMaxReach = isRecovery ? legExtensionMax * 0.8f : legExtensionMax;
        float comfortZone = effectMaxReach * 0.9f;
        AttachmentPoint best = new AttachmentPoint { isValid = false };
        float bestScore = float.MinValue;

        foreach (var ap in attachmentPoints)
        {
            float distFromSegment = Vector2.Distance(segment.position, ap.point);
            bool isWeb = ap.collider.gameObject.layer == webLayer;

            if (distFromSegment > effectMaxReach || distFromSegment < legExtensionMin) continue;
            
            // On webs, allow stepping much closer to the head to ensure we don't "miss" the strand segment under us
            float effectSafeArea = isWeb ? headSafeArea * 0.5f : headSafeArea;
            if (distFromSegment < effectSafeArea) continue; 
            
            if (IsPointTooCloseToOtherFeet(ap.point, leg)) continue;

            // Significant stride check: 
            // Only enforce a 'Minimum jump distance' if the leg is already planted.
            // If the leg is detached (in the air), any point is better than nothing!
            if (leg.isPlanted && !leg.isStepping && inputDirection.sqrMagnitude > 0.01f)
            {
                if (Vector2.Distance(leg.currentFootPos, ap.point) < minStride * (isWeb ? 0.5f : 1.0f))
                    continue;
            }

            float localX = segment.InverseTransformPoint(ap.point).x;
            float score = 0f;

            // 1. Distance to predictive goal
            score -= Vector2.Distance(idealPos, ap.point) * 2.0f;
            
            // 4. Directional Progressive Check (RELAXED for detached legs)
            if (steeredMovementDir.sqrMagnitude > 0.05f && leg.isPlanted)
            {
                Vector2 stepVec = (ap.point - leg.currentFootPos);
                float progress = Vector2.Dot(stepVec, steeredMovementDir.normalized);
                if (progress < -minStride * 0.1f) continue; 
            }

            // 3. Extension preference
            float extDiff = Mathf.Abs(distFromSegment - comfortZone);
            score -= extDiff * 1.5f;

            // 5. Huge Step Penalty
            float moveDist = Vector2.Distance(leg.currentFootPos, ap.point);
            if (moveDist > maxReach * 1.2f) score -= 10f;

            // 4. Directional Consistency (REDUCED for detached legs)
            if (smoothedInput.sqrMagnitude > 0.05f)
            {
                Vector2 shoulderPos = (Vector2)segment.TransformPoint(restingFootOffsets[idx]);
                Vector2 toPointRel = ap.point - shoulderPos;
                Vector2 inputNorm = smoothedInput.normalized;
                
                float projection = Vector2.Dot(toPointRel, inputNorm);
                
                // If in the air, value ANY point highly, but slightly favor the input direction.
                float inputBiasMult = leg.isPlanted ? 1.0f : 0.2f; 
                
                score += (projection * 5f) * inputBiasMult; 
                score += (Vector2.Dot(toPointRel.normalized, inputNorm) * 8f) * inputBiasMult;
            }

            // 5. Directional/Orientation Scores
            // Removed localY penalty and normal-up bias to avoid 'upward distinction'
            score += Mathf.Abs(localX) * 2f;
            // score += Vector2.Dot(ap.normal, headT.up) * 3f; 

            if (score > bestScore) { bestScore = score; best = ap; }
        }
        return best;
    }

    bool HasValidTarget(CentipedeLegIK leg, bool isRecovery = false)
    {
        Vector2 inputNorm = smoothedInput.normalized;
        bool hasInput = smoothedInput.sqrMagnitude > 0.05f;

        Transform segment = leg.bodyTransform != null ? leg.bodyTransform : headT;

        int webLayer = LayerMask.NameToLayer("Web");
        foreach (var ap in attachmentPoints)
        {
            float distFromSegment = Vector2.Distance(segment.position, ap.point);
            bool isWeb = ap.collider.gameObject.layer == webLayer;

            float effectMaxReachTarget = isRecovery ? legExtensionMax * 0.75f : legExtensionMax;
            if (distFromSegment > effectMaxReachTarget || distFromSegment < legExtensionMin) continue;
            
            float effectSafeArea = isWeb ? headSafeArea * 0.5f : headSafeArea;
            if (distFromSegment < effectSafeArea) continue; 

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

    bool IsPointTooCloseToOtherFeet(Vector2 point, CentipedeLegIK excludeLeg)
    {
        int myIdx = System.Array.IndexOf(legs, excludeLeg);
        // Only check neighboring legs for centipede performance
        int range = 3; 
        int start = Mathf.Max(0, myIdx - range);
        int end = Mathf.Min(legs.Length, myIdx + range + 1);

        for (int i = start; i < end; i++)
        {
            var leg = legs[i];
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

        // userLiftInput is the degree to which we are pushing AWAY from the current surface normal.
        // This makes the 'raise' logic orientation-agnostic (works on walls/ceilings).
        float userLiftInput = Mathf.Clamp01(Vector2.Dot(smoothedInput.normalized, currentSurfaceNormal));

        // 0. Synchronize all legs and calculate local grip context
        int currentPlanted = 0;
        foreach (var leg in legs) if (leg.isPlanted) currentPlanted++;
        int safetyAnchorCount = Mathf.Max(1, (int)(legs.Length * 0.1f)); 

        foreach (var leg in legs) 
        {
            leg.SyncToSurface();

            // FORCE DETACH: If a foot is stretched too far, it MUST lose grip.
            // When lifting/flying, we preserve the metachronal logic to ensure a front-to-back "peeling" effect,
            // but we no longer shrink the limit intentionally to avoid premature tucking.
            float peelDistance = legExtensionMax * 1.05f; 

            bool overExtended = leg.GetExtension() > peelDistance;
            
            // Metachronal peeling: A leg is more likely to detach if the legs in front of it are already detached
            int legIdx = System.Array.IndexOf(legs, leg);
            bool neighborsDetached = true;
            if (legIdx > 0 && userLiftInput > 0.1f)
            {
                // Is the leg in front of me planted? If so, wait.
                if (legs[legIdx - 1].isPlanted && legs[legIdx - 1].GetExtension() < peelDistance * 0.9f)
                    neighborsDetached = false;
            }

            // ONLY tuck if we are overExtended AND we have no better place to step to.
            if (leg.isPlanted && !leg.isStepping && overExtended && neighborsDetached)
            {
                // If we are overExtended but still have a valid target, we should STEP rather than tuck.
                if (HasValidTarget(leg, false))
                {
                    StepLeg(leg, false);
                }
                else if (currentPlanted > safetyAnchorCount) // SAFETY ANCHOR: Don't let the last 10% of legs detach
                {
                    leg.isPlanted = false;
                    leg.plantedTransform = null;
                    currentPlanted--;
                }
            }
        }

        int totalPlanted = 0;
        foreach (var leg in legs) if (leg.isPlanted) totalPlanted++;

        // ── AIRPLANE / GROUNDING LOGIC ──
        float groundedness = (float)totalPlanted / Mathf.Max(1, legs.Length);

        // REALISTIC PHYSICS: Linear traction (lift, gravity cancel, propulsion) is strictly grounded.
        // If zero feet are planted, linearTraction is 0. No floating.
        float linearTraction = Mathf.Clamp01(groundedness / Mathf.Max(0.001f, fallThreshold));
        
        // POSTURAL INFLUENCE: Rotation muscle retains a baseline if active input is provided.
        float controlFloor = (smoothedInput.sqrMagnitude > 0.01f) ? minControlRetention : 0.0f;
        float muscleInfluence = Mathf.Clamp(groundedness / Mathf.Max(0.001f, fallThreshold), controlFloor, 1.0f);

        // Alias for the existing code logic
        float groundingSlowdown = linearTraction; 


        // Identify the "Influence Group" - only the legs on the Head transform
        int planted = 0;
        Vector2 avgFootPos = Vector2.zero;
        Vector2 normalSum  = Vector2.zero;
        int totalHeadLegs = 0;

        for (int i = 0; i < legs.Length; i++)
        {
            var leg = legs[i];
            // Only consider legs attached to the HEAD or very close to it for driving
            if (leg.bodyTransform != headT) continue;
            
            totalHeadLegs++;
            if (!leg.isPlanted) continue;
            
            avgFootPos   += leg.currentFootPos;
            normalSum    += leg.plantedNormal;
            planted++;
        }

        // --- CAR STEERING FALLBACK ---
        // If the front tires (head legs) aren't touching, look back through the segments
        // until we find a "wheelbase" that is touching.
        int searchLimit = Mathf.Min(legs.Length, 12); 
        if (planted == 0)
        {
            for (int i = 0; i < searchLimit; i++)
            {
                if (legs[i].isPlanted)
                {
                    avgFootPos += legs[i].currentFootPos;
                    normalSum += legs[i].plantedNormal;
                    planted++;
                    // Stop once we have some grounded context or we hit the limit
                    if (planted >= 4) break; 
                }
            }
        }

        // SAFETY: If we still have no planted context, provide a fallback instead of bailing.
        // This ensures the head remains controllable even when 100% airborne.
        if (planted == 0)
        {
            avgFootPos = headRb.position;
            normalSum = currentSurfaceNormal; 
            planted = 1; // Virtual anchor for math stability
        }

        if (totalHeadLegs == 0) totalHeadLegs = 4; // Grip safety

        // Calculate Groundedness (How many legs are on solid surfaces vs webs)
        int webLayer = LayerMask.NameToLayer("Web");
        int groundPlanted = 0;
        foreach (var leg in legs)
        {
            if (leg.isPlanted && leg.plantedTransform != null && leg.plantedTransform.gameObject.layer != webLayer)
                groundPlanted++;
        }
        // groundFactor is 1.0 on solid floor, ~0.0 on webs.
        float groundFactor = (float)groundPlanted / Mathf.Max(1, planted);

        // Multi-leg stability on strands: 
        // If we have many legs on the SAME strand, we can move more confidently.
        int maxOnSameStrand = 0;
        strandCounts.Clear();
        foreach (var leg in legs)
        {
            if (leg.isPlanted && leg.plantedTransform != null)
            {
                Collider2D col = leg.plantedTransform.GetComponent<Collider2D>();
                if (col != null)
                {
                    strandCounts[col] = strandCounts.ContainsKey(col) ? strandCounts[col] + 1 : 1;
                    if (strandCounts[col] > maxOnSameStrand) maxOnSameStrand = strandCounts[col];
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
        Vector2 center = (avgFootPos / Mathf.Max(1, planted));
        for (int i = 0; i < searchLimit; i++)
        {
            if (i >= legs.Length) break;
            var leg = legs[i];
            
            // Only legs that contributed to the average
            if (leg.bodyTransform != headT && planted > 0)
            {
                // check if we used fallback
                bool usedInAvg = false;
                for(int k=0; k<Mathf.Min(legs.Length, 12); k++){
                    if(legs[k].isPlanted) { usedInAvg = true; break; }
                }
                if (!usedInAvg) continue;
            }

            if (!leg.isPlanted) continue;
            
            Vector2 relPos = (Vector2)leg.currentFootPos - center;
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

            // --- FRONT-FOOT NORMAL ALIGNMENT ---
            // Reduced surface bias to allow 'airplane' orientation in flight.
            float surfaceAlignmentAngle = Mathf.Atan2(avgNormal.y, avgNormal.x) * Mathf.Rad2Deg - 90f;
            
            // Further reduce bias when steering away from the surface
            float steeringAway = 1.0f - Mathf.Clamp01(Vector2.Dot(smoothedInput.normalized, avgNormal));
            
            // INCREASED reaching sensitivity: when user moves away from surface, bias significantly towards their stick
            float alignmentBias = Mathf.Lerp(0.05f, 0.35f, groundedness) * steeringAway; 
            
            targetAngle = Mathf.LerpAngle(targetAngle, surfaceAlignmentAngle, alignmentBias); 
            
            // 2b. CAR STEERING: Blend input direction into the rotation
            // SMOOTHED INFLUENCE: Gradually blend in/out when stick is moved or released
            float targetInfluence = (smoothedInput.sqrMagnitude > 0.01f) ? 1.0f : 0.0f;
            stickInfluence = Mathf.MoveTowards(stickInfluence, targetInfluence, Time.fixedDeltaTime * 4.0f); // 0.25s transition

            if (stickInfluence > 0.001f)
            {
                // WORLD SPACE INPUT: (Right=0, Up=90)
                float rawInputAngle = Mathf.Atan2(smoothedInput.y, smoothedInput.x) * Mathf.Rad2Deg;
                
                // Align world-input (stick direction) to the Head's "Forward" interpretation
                masterSteerTarget = rawInputAngle - headForwardOffset;
                float worldForwardGoal = masterSteerTarget;

                // --- DIRECT STEERING ---
                float reachFactor = Vector2.Dot(smoothedInput.normalized, avgNormal);
                // REACH-BOOST: When reaching away (reachFactor > 0), we want the stick to be 
                // the authority (98%). When moving parallel, we allow more sprawl-bias.
                float steerWeight = Mathf.Lerp(0.7f, 0.98f, Mathf.Clamp01(reachFactor)); 
                
                // Blend from current sprawl-target to the stick-target based on smoothed influence
                targetAngle = Mathf.LerpAngle(targetAngle, worldForwardGoal, stickInfluence * steerWeight); 
            }
            else
            {
                // Fade out the master steer target to the current normal when no input
                masterSteerTarget = Mathf.LerpAngle(masterSteerTarget, Mathf.Atan2(avgNormal.y, avgNormal.x) * Mathf.Rad2Deg - 90f, Time.fixedDeltaTime * 2f);
            }

            // PREVENT CRUMPLE: Clamp head rotation relative to the first body segment
            if (bodySegments != null && bodySegments.Length > 0 && bodySegments[0] != null)
            {
                float segmentAngle = bodySegments[0].eulerAngles.z;
                float relative = Mathf.DeltaAngle(segmentAngle, targetAngle);
                // Head limit: 120 degrees relative to S0. 
                // If it hits this, the 'strain' will propagate to the segments in the physics loop.
                targetAngle = segmentAngle + Mathf.Clamp(relative, -120f, 120f); 
            }
        }
        
        currentSurfaceNormal = avgNormal;
        float speedMagnitude = headRb != null ? headRb.linearVelocity.magnitude : 0f;
        
        // Faster rotation when moving, boosted even further when reaching for 'snappy' control
        float reachFactorNow = Vector2.Dot(smoothedInput.normalized, avgNormal);
        float reachBoost = Mathf.Lerp(1.0f, 2.5f, Mathf.Clamp01(reachFactorNow)); 
        float speedScale = headRb != null ? headRb.linearVelocity.magnitude : 0f;
        float rotSpeedCurve = rotationSpeed * Mathf.Lerp(1.0f, 4.0f, speedScale / 8f) * reachBoost;
        
        Quaternion stableRot = Quaternion.Slerp(headT.rotation, Quaternion.Euler(0, 0, targetAngle), Time.fixedDeltaTime * rotSpeedCurve);

        // 3. Calculate target position using the stable frame to transform offsets.
        Vector2 basePos = Vector2.zero;
        int activeInfluenceCount = 0;
        for (int i = 0; i < searchLimit; i++)
        {
            var leg = legs[i];
            if (leg.bodyTransform != headT && planted > 0)
            {
                 // ensure we only use the fallback if necessary
                 bool onHead = false;
                 for(int j=0; j<legs.Length; j++) if(legs[j].bodyTransform == headT && legs[j].isPlanted) { onHead = true; break; }
                 if(onHead) continue;
            }

            if (!leg.isPlanted) continue;

            Vector2 worldOffset = stableRot * (Vector3)restingFootOffsets[i];
            basePos += (leg.currentFootPos - worldOffset);
            activeInfluenceCount++;
        }
        
        Vector2 targetPos = activeInfluenceCount > 0 ? (basePos / activeInfluenceCount) : (Vector2)headRb.position;
        
        // --- Apply Body Height & Lead to the target frame ---
        float confidence = Mathf.Max(groundFactor, strandFactor * 0.7f);
        float effectHeight = bodyHeight * Mathf.Lerp(0.2f, 1.0f, confidence);
        float effectLead   = bodyLead   * Mathf.Lerp(0.4f, 1.0f, confidence);
        
        targetPos += avgNormal * effectHeight;

        // --- LOCOMOTION ENGINE ---
        
        // 1. Dynamic Gravity & Propulsion
        // As long as we are above 20% grounding, we keep gravity near 1.0 to allow 'flight'.
        // Below 20%, we start getting heavy for the 'physical fall'.
        float gravityFactor = 1.0f;
        if (groundedness < fallThreshold)
        {
            gravityFactor = Mathf.Lerp(15.0f, 1.0f, groundingSlowdown);
        }
        headRb.gravityScale = gravityFactor;
        
        // Fallback Grip: If head is in air, use body segments for propulsion
        float headGrip = (float)planted / Mathf.Max(1, totalHeadLegs);
        float bodyGrip = (float)totalPlanted / legs.Length;
        float gripFactor = Mathf.Max(headGrip, bodyGrip * 0.5f) * groundingSlowdown;


        // 2. Head Rotation (Steering)
        float finalHeading = targetAngle;
        if (steeredMovementDir.sqrMagnitude > 0.01f)
        {
            finalHeading = Mathf.LerpAngle(targetAngle, currentSteerHeading, 0.7f); 
            
            if (showDebug) 
            {
                // Debug.Log($"[Centipede] SteerH: {currentSteerHeading:F1}° | FinalH: {finalHeading:F1}° | Grip: {gripFactor:P0}");
            }
        }
        
        // Final sprite rotation Z is just the mathematical world heading
        // POSTURAL CONTROL: Uses 'muscleInfluence' to maintain player aim even in a fall.
        float muscleStiffness = 1.0f + Mathf.Clamp01(userLiftInput) * 2.0f;
        if (groundedness < 0.01f) muscleStiffness *= airborneMuscleStiffness;
        
        headT.rotation = Quaternion.Slerp(headT.rotation, Quaternion.Euler(0, 0, finalHeading), Time.fixedDeltaTime * rotSpeedCurve * muscleStiffness * muscleInfluence);
        headRb.freezeRotation = true;

        // 3. Movement (Propulsion - NOSE PULL)
        // We calculate 'Forward' relative to the transform's base coordinate system using the offset
        Vector2 localForward = (Quaternion.Euler(0, 0, headForwardOffset) * Vector2.right);
        Vector2 worldForward = headT.TransformDirection(localForward);
        
        // Propulsion pulls the nose along the calculated steer direction
        // Boosted force when reaching/climbing:
        float powerMult = 1.0f + Mathf.Clamp01(userLiftInput) * 0.5f;
        Vector2 locomotionForce = steeredMovementDir * (usePhysicsSegments ? 300f : 150f) * bodySmoothing * gripFactor * powerMult;
        
        // 4. Ride Height (High-Pressure Hydraulic Suspension)
        Vector2 relativePos = headRb.position - (avgFootPos / Mathf.Max(1, planted));
        float currentHeight = Vector2.Dot(relativePos, avgNormal);
        float heightError = bodyHeight - currentHeight;
        
        // Massive lift to fight heavy gravity (25x scale)
        // MUSCLE: Increase lift traction when the user is actively reaching
        float muscleTension = 1.0f + Mathf.Clamp01(userLiftInput) * 1.5f;
        float liftTraction = (usePhysicsSegments ? 800f : 400f) * muscleTension; 
        float dampScalar = (usePhysicsSegments ? 150f : 50f) * muscleTension;
        
        // PD-style spring: Force = K*error - D*velocity
        // Fade out the spring force if we are falling.
        // CRITICAL: If lifting, don't let the suspension spring pull the head BACK DOWN to the ground.
        float vertVel = Vector2.Dot(headRb.linearVelocity, avgNormal);
        float liftForceMag = (heightError * liftTraction) - (vertVel * dampScalar);
        if (userLiftInput > 0.1f) liftForceMag = Mathf.Max(0, liftForceMag);
        
        Vector2 springForce = avgNormal * liftForceMag * groundingSlowdown; 
        
        // Add a 'Ground Compression' factor: limit velocity downward if we are already low
        if (heightError < -0.1f && Vector2.Dot(headRb.linearVelocity, avgNormal) < 0)
        {
            headRb.linearVelocity -= avgNormal * Vector2.Dot(headRb.linearVelocity, avgNormal) * 0.5f;
        }

        // 5. DISTRIBUTED LIFT & GRAVITY CANCEL
        Vector2 noseTip = (Vector2)headT.TransformPoint(localForward * 0.5f);

        // Braking... (Reduced strength to prevent tail anchoring)
        if (smoothedInput.sqrMagnitude < 0.01f && totalPlanted > 0)
        {
            float brakeStrength = Mathf.Lerp(5.0f, 25.0f, (float)totalPlanted / legs.Length);
            headRb.AddForce(-headRb.linearVelocity * headRb.mass * brakeStrength, ForceMode2D.Force);
            locomotionForce = Vector2.zero;
        }

        // --- AIRPLANE LIFT (PITCH AWAY) ---
        // Apply force AWAY from the current surface normal.
        // REALISTIC PHYSICS: Linear propulsion and lift are 100% tied to grounding. 
        // No feet = zero thrust. This prevents flight.
        Vector2 airplaneLift = currentSurfaceNormal * userLiftInput * liftPotency * headRb.mass * (groundedness > 0 ? groundingSlowdown : 0f);
        
        // REALISTIC PHYSICS: Gravity neutralization requires grounded feet.
        Vector2 gravNeutralizer = -Physics2D.gravity * headRb.gravityScale * headRb.mass * (groundedness > 0 ? groundingSlowdown : 0f);
        headRb.AddForce(springForce + gravNeutralizer + airplaneLift, ForceMode2D.Force);
        headRb.AddForceAtPosition(locomotionForce * (groundedness > 0 ? 1.0f : 0f), noseTip, ForceMode2D.Force);

        // --- Per-Segment suspension to prevent launching & floating ---
        if (usePhysicsSegments && bodySegments != null)
        {
            // Reset segments
            foreach (var seg in bodySegments)
            {
                if (seg == null) continue;
                Rigidbody2D rb = seg.GetComponent<Rigidbody2D>();
                if (rb != null)
                {
                    rb.gravityScale = gravityFactor; // Sync gravity with head to avoid disjointed falling
                    rb.linearDamping = 1.0f;
                }
            }

            // Apply lift ONLY to segments with planted feet
            HashSet<Rigidbody2D> processedSegments = new HashSet<Rigidbody2D>();
            foreach (var leg in legs)
            {
                if (leg.isPlanted && leg.bodyTransform != headT && leg.bodyTransform != null)
                {
                    Rigidbody2D segRb = leg.bodyTransform.GetComponent<Rigidbody2D>();
                    if (segRb != null && !processedSegments.Contains(segRb))
                    {
                        processedSegments.Add(segRb);

                        int segIdx = System.Array.IndexOf(bodySegments, leg.bodyTransform);
                        float progress = (float)segIdx / bodySegments.Length;

                        // Local curvature context
                        Vector2 segNormalSum = Vector2.zero;
                        int segPlantedCount = 0;
                        foreach (var l in legs) {
                            if (l.isPlanted && l.bodyTransform == leg.bodyTransform) {
                                segNormalSum += l.plantedNormal;
                                segPlantedCount++;
                            }
                        }
                        Vector2 localSegNormal = segPlantedCount > 0 ? segNormalSum.normalized : avgNormal;
                        float segGrip = segPlantedCount > 1 ? 1.0f : 0.6f;

                        // ── SEGMENT PROPULSION (COOPERATION) ──
                        // Grounded legs push the body in the direction of input
                        float pushForce = 400f * powerMult * groundingSlowdown;
                        segRb.AddForce(steeredMovementDir * segRb.mass * pushForce * segGrip, ForceMode2D.Force);

                        // Calculate height
                        float segCurrentHeight = Vector2.Dot(segRb.position - leg.currentFootPos, localSegNormal);
                        float segHeightError = bodyHeight - segCurrentHeight;
                        
                        float segVertVel = Vector2.Dot(segRb.linearVelocity, localSegNormal);
                        float segLiftMag = (segHeightError * liftTraction * 0.5f) - (segVertVel * 100f);
                        segLiftMag *= segRb.mass * segGrip;
                        segLiftMag = Mathf.Min(segLiftMag, 1500f * segRb.mass); 

                        Vector2 segGravNeutralizer = -Physics2D.gravity * segRb.gravityScale * segRb.mass * (userLiftInput > 0.1 ? 1.0f : segGrip);
                        segRb.AddForce(localSegNormal * (segLiftMag * groundingSlowdown) + segGravNeutralizer, ForceMode2D.Force);
                        
                        // Orientation
                        float groundAngle = Mathf.Atan2(localSegNormal.y, localSegNormal.x) * Mathf.Rad2Deg - 90f;
                        
                        // ── DISTRIBUTED POSTURAL PROPAGATION ──
                        // If the segment in front of us is rotated significantly, pull this segment with it.
                        float aheadAngle = (segIdx == 0) ? headT.eulerAngles.z : bodySegments[segIdx-1].eulerAngles.z;
                        float jointStrain = Mathf.DeltaAngle(segRb.rotation, aheadAngle);
                        
                        // Joint Comfort Zone: We only propagate rotation if the joint is straining past a limit
                        float strainThreshold = maxSegmentBend * 0.4f; 
                        float strainFactor = Mathf.Clamp01((Mathf.Abs(jointStrain) - strainThreshold) / (maxSegmentBend - strainThreshold));
                        
                        // Blend Stick Influence with Ground Alignment
                        // If stick is active, and joint is straining, override the ground-alignment to curl the body.
                        float targetBodyAngle = Mathf.LerpAngle(groundAngle, masterSteerTarget, stickInfluence * strainFactor * muscleInfluence);
                        
                        float angleError = Mathf.DeltaAngle(segRb.rotation, targetBodyAngle);
                        float torqueStrength = 80f * (1.0f + stickInfluence * 2.0f); // Stiffen up when steering
                        segRb.AddTorque(angleError * torqueStrength * segRb.mass, ForceMode2D.Force);
                        
                        // Tight Physics Clamp: Don't let the joint physically exceed the design limit
                        if (Mathf.Abs(jointStrain) > maxSegmentBend)
                        {
                            segRb.rotation = aheadAngle - Mathf.Sign(jointStrain) * maxSegmentBend;
                        }
                        
                        // High-pressure lift for body segments when the head is lifting
                        // MUSCLE: Body segments now actively supporting the head's reach
                        float muscleArchSupport = userLiftInput * muscleArchForce * segRb.mass * Mathf.Clamp01(1.2f - progress);
                        segRb.AddForce(localSegNormal * muscleArchSupport, ForceMode2D.Force);
                        
                        // NOSE-TENSION: Segments also pull TOWARDS the head to keep the body compact and muscular
                        Vector2 toHead = ((Vector2)headT.position - segRb.position).normalized;
                        float segContraction = contractionForce * userLiftInput * segRb.mass * (1.0f - progress);
                        segRb.AddForce(toHead * segContraction, ForceMode2D.Force);

                        segRb.linearDamping = Mathf.Lerp(10f, 2f, progress); 
                        segRb.angularDamping = Mathf.Lerp(10f, 2f, progress); 
                    }
                }
            }
        }

        
        // Physics Damping: Simulate friction of feet on ground
        // Drastically reduced maximum damping (was 20.0f) to prevent tail anchorage.
        // Also reduce damping further when the player is trying to move.
        float activeMultiplier = smoothedInput.sqrMagnitude > 0.01f ? 0.35f : 1.0f;
        headRb.linearDamping = Mathf.Lerp(1.5f, 8.0f, (float)totalPlanted / legs.Length) * activeMultiplier;

        // 6. Apply Weight to Webs
        // This transfers the spider's physical presence onto the strands so they sag and react.
        if (spiderWeightMult > 0.01f && totalPlanted > 0)
        {
            float weightPerLeg = (headRb.mass * Mathf.Abs(Physics2D.gravity.y) * spiderWeightMult * 2.0f) / Mathf.Max(1, totalPlanted);
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

    private CentipedeLegIK GetLegForSegment(Transform seg)
    {
        foreach (var l in legs) if (l.bodyTransform == seg) return l;
        return null;
    }

    public Vector2 GetCurrentSurfaceNormal() => currentSurfaceNormal;

    public Vector2 GetRestingOffset(int index)
    {
        if (restingFootOffsets != null && index >= 0 && index < restingFootOffsets.Length)
            return restingFootOffsets[index];
        return Vector2.zero;
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

            Transform segment = leg.bodyTransform != null ? leg.bodyTransform : t;
            Vector2 segmentPos = segment.position;

            Gizmos.color = leg.isPlanted ? Color.cyan : Color.magenta;
            Gizmos.DrawLine(segmentPos, leg.currentFootPos);
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
                Vector2 rest = segment.TransformPoint(restingFootOffsets[i]);
                Gizmos.color = new Color(1, 1, 1, 0.3f);
                Gizmos.DrawWireSphere(rest, 0.3f);

                if (smoothedInput.sqrMagnitude > 0.01f)
                {
                    float maxReach = leg.thighLength + leg.shinLength;
                    Vector2 ideal = (Vector2)segment.TransformPoint(restingFootOffsets[i])
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

        Gizmos.color = new Color(0, 1, 0, 0.4f);
        foreach (var ap in attachmentPoints) Gizmos.DrawSphere(ap.point, 0.1f);

        Gizmos.color = Color.yellow;
        Vector2 clampedTip = headPos + steeredMovementDir * 5f;
        Gizmos.DrawLine(headPos, clampedTip); // WHERE WE ARE GOING (Clamped)
        Gizmos.DrawWireSphere(clampedTip, 0.2f);
        
        Gizmos.color = new Color(1f, 0.6f, 0f, 1f); // VIBRANT ORANGE
        Vector2 rawTip = headPos + smoothedInput.normalized * 6f;
        Gizmos.DrawLine(headPos, rawTip); // WHAT THE PLAYER WANTS (Raw Stick)
        Gizmos.DrawSphere(rawTip, 0.25f);

        Gizmos.color = Color.cyan;
        Vector2 localFwd = (Quaternion.Euler(0, 0, headForwardOffset) * Vector2.right);
        Vector2 worldVisFwd = t.TransformDirection(localFwd);
        Vector2 visTip = headPos + worldVisFwd * 4.5f;
        Gizmos.DrawLine(headPos, visTip); // Actual visual face direction
        Gizmos.DrawWireSphere(visTip, 0.15f);
        
        Gizmos.color = Color.red;
        Vector2 noseTip = t.TransformPoint(localFwd * 0.5f);
        Gizmos.DrawWireSphere(noseTip, 0.1f); // Force application point

        Gizmos.color = new Color(0, 1, 0, 0.08f);
        if (bodySegments != null)
        {
            foreach(Transform seg in bodySegments)
            {
                if (seg != null)
                {
                    Gizmos.DrawWireSphere(seg.position, legExtensionMax);
                    Gizmos.DrawWireSphere(seg.position, legExtensionMin);
                }
            }
        }
        else
        {
            Gizmos.DrawWireSphere(headPos, legExtensionMax);
            Gizmos.DrawWireSphere(headPos, legExtensionMin);
        }
        
        Gizmos.color = new Color(1, 0, 0, 0.1f);
        Gizmos.DrawWireSphere(headPos, headSafeArea);
    }
}
