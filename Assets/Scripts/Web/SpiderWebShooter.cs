using UnityEngine;
using System.Collections;

// ───────────────────────────────────────────────────────────
//  IDEAL SETTINGS:
//  SpiderWebShooter: maxShootRange = 50, segmentLength = (auto), maxWebSegments = 50-100
//  WebSegment Prefab: Mass = 0.1, Linear Damping = 5, Angular Damping = 5, Gravity = 0.5
// ───────────────────────────────────────────────────────────
public class SpiderWebShooter : MonoBehaviour
{
    public GameObject webPrefab;
    public Transform webStartPoint;
    public int maxWebSegments = 10;
    public LayerMask defaultLayer;
    public float segmentLength = 1f;
    public float overlapRadius = 0.2f; // Radius for checking overlaps
    public int stringId = 0;
    public BoxCollider2D webRb;
    public float maxShootRange = 50f;
    [Range(.1f, 1.2f)] public float tensionFactor = 1.01f; // Low = Tight, High = Slack
    public float rappellingWinchSpeed = 18f;
    private float safetyRange;
    private DistanceJoint2D tetherJoint;
    private Transform currentTetherAnchor;

    private GameObject activeWebString;
    private GameObject lastSegment;
    private bool isHoldingWeb = false;
    private int currentSegmentCount = 0;
    private Vector2 lastExtrusionPos;
    public bool IsHoldingWeb => isHoldingWeb;
    public GameObject ActiveWebString => activeWebString;

    // ── Placement Mode ──────────────────────────────────────
    // When the player clicks a surface within leg reach, the web
    // is anchored there and trails from the spider's body as it moves.
    private bool isPlacementMode = false;
    public bool IsPlacementMode => isPlacementMode;
    private SpiderControllerIK spiderController;

    // Returns true if the spider is completely unplanted OR if more planted feet are gripping the active strand than other surfaces
    public bool IsRappelling
    {
        get
        {
            if (!isPlacementMode || lastSegment == null || spiderController == null || activeWebString == null) return false;
            int onWebStrand = 0;
            int onOther = 0;
            foreach (var leg in spiderController.legs)
            {
                if (leg.isPlanted)
                {
                    if (leg.plantedTransform != null && leg.plantedTransform.IsChildOf(activeWebString.transform))
                        onWebStrand++;
                    else
                        onOther++;
                }
            }
            return (onWebStrand + onOther == 0) || (onWebStrand > onOther);
        }
    }

    // Single DistanceJoint2D (rope mode) that physically attaches the trailing web
    // end to the spider's ACTUAL Dynamic head rigidbody. maxDistanceOnly = true means
    // the spider can move freely within range but can't exceed the web's reach.
    // Re-pointed to each new last segment — O(1), no per-segment joints.
    private DistanceJoint2D placementRope;
    private Rigidbody2D placementRopeBody; // The body the rope lives on (headRb)
    private WebStringRenderer activeRenderer;

    void Awake()
    {
        // Initialize tethering physics immediately so other scripts can access it
        tetherJoint = gameObject.AddComponent<DistanceJoint2D>();
        
        // Prevent the automatically added Rigidbody2D from causing the parent to fall forever
        Rigidbody2D rb = GetComponent<Rigidbody2D>();
        if (rb != null)
        {
            rb.bodyType = RigidbodyType2D.Kinematic;
        }

        tetherJoint.enabled = false;
        tetherJoint.autoConfigureDistance = false;
        tetherJoint.distance = 0.1f;
        tetherJoint.maxDistanceOnly = false; // Keep it tight
        currentTetherAnchor = webStartPoint != null ? webStartPoint : transform;
    }

    void Start()
    {
        if (webRb != null) segmentLength = webRb.size.x; // size.x is the FULL width
        spiderController = GetComponent<SpiderControllerIK>();
        // Using LegExtensionMax as our safety range as requested
        if (spiderController != null) safetyRange = spiderController.legExtensionMax;

        // Create placement rope on the spider's ACTUAL Dynamic headRb,
        // NOT on this gameObject (which has a Kinematic rb from the DistanceJoint2D).
        // Uses maxDistanceOnly so the spider can move freely but can't exceed the web's reach.
        if (spiderController != null && spiderController.headRb != null)
        {
            placementRopeBody = spiderController.headRb;
            placementRope = placementRopeBody.gameObject.AddComponent<DistanceJoint2D>();
            placementRope.autoConfigureConnectedAnchor = false;
            placementRope.anchor = Vector2.zero;
            placementRope.connectedAnchor = Vector2.zero;
            placementRope.autoConfigureDistance = false;
            placementRope.maxDistanceOnly = true; // Rope mode: free within range, constrained at max
            placementRope.distance = segmentLength;
            placementRope.enabled = false;
        }
    }

    void Update()
    {
        if (Input.GetMouseButtonDown(1)) // Right Click to shoot/place/finalize
        {
            if (!isHoldingWeb && !isPlacementMode)
            {
                // Check if the click target is on a surface within leg reach —
                // if so, use placement mode instead of ranged shot.
                if (TryBeginPlacement())
                    return; // Placement started; skip ranged shot

                StartCoroutine(BeginExtrusion());
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
        {
            UpdatePlacement();
        }
        else if (isHoldingWeb)
        {
            UpdateExtrusion();
        }
    }

    private IEnumerator BeginExtrusion()
    {
        isHoldingWeb = true;
        currentSegmentCount = 0;
        lastSegment = null; // Clean slate
        Vector2 mousePos = GetMouseWorldPos();
        Vector2 startPos = (Vector2)webStartPoint.position;
        Vector2 direction = (mousePos - startPos).normalized;

        // 1. Long range raycast to find where the "Shot" hits
        RaycastHit2D[] hits = Physics2D.RaycastAll(startPos, direction, maxShootRange, defaultLayer | LayerMask.GetMask("Web"));
        RaycastHit2D hit = new RaycastHit2D();
        
        // Find the closest hit that isn't a web inside our safety radius
        float minD = float.MaxValue;
        foreach (var h in hits)
        {
            float d = Vector2.Distance(h.point, startPos);
            if (h.collider.gameObject.layer == LayerMask.NameToLayer("Web") && d < safetyRange) continue;
            
            if (d < minD)
            {
                minD = d;
                hit = h;
            }
        }
        
        Vector2 endPoint = hit.collider != null ? hit.point : startPos + (direction * (maxShootRange * 0.5f));

        // --- Hierarchy Merging ---
        // If we hit an existing web, make this new strand a child of that web's container
        if (hit.collider != null && hit.collider.gameObject.layer == LayerMask.NameToLayer("Web"))
        {
            activeWebString = new GameObject("Branch_" + stringId);
            activeWebString.transform.SetParent(hit.collider.transform.parent);
        }
        else
        {
            activeWebString = new GameObject("LiveWebString_" + stringId);
        }
        activeWebString.AddComponent<WebStringRenderer>();

        float totalDist = Vector2.Distance(startPos, endPoint);
        int numSegments = Mathf.Clamp(Mathf.CeilToInt(totalDist / segmentLength), 1, maxWebSegments);

        // 2. Build segments from the WALL back to the SPIDER
        // This ensures the chain is: Wall -> ... -> Spider
        Vector2 backDir = (startPos - endPoint).normalized;
        Vector2 currentSpawnPos = endPoint;

        for (int i = 0; i < numSegments; i++)
        {
            GameObject segmentGO = SpawnSegment(currentSpawnPos);
            WebSegment ws = segmentGO.GetComponent<WebSegment>();
            
            float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
            segmentGO.transform.rotation = Quaternion.Euler(0, 0, angle);

            if (i == 0) 
            {
                // First segment (at the target) attaches to the surface
                if (hit.collider != null) ws.ForceAttachStart(hit.collider, hit.point);
                else ws.CheckCurrentContact(); 
            }

            currentSpawnPos += backDir * segmentLength;
        }

        lastExtrusionPos = startPos;
        UpdateTether();
        yield return null; 
    }

    private void UpdateExtrusion()
    {
        if (lastSegment == null) return;

        // Tension Control: Only add segments when the SPIDER physically moves a full length
        Vector2 anchorPos = currentTetherAnchor != null ? (Vector2)currentTetherAnchor.position : (Vector2)webStartPoint.position;
        float travelDist = Vector2.Distance(lastExtrusionPos, anchorPos);
        
        if (travelDist > segmentLength * tensionFactor) 
        {
            if (currentSegmentCount < maxWebSegments)
            {
                SpawnSegment(anchorPos);
                lastExtrusionPos = anchorPos;
                UpdateTether();
            }
        }
        else
        {
            UpdateTether();
        }
    }

    public void SetTetherAnchor(Transform anchor)
    {
        currentTetherAnchor = anchor;
        UpdateTether();
    }

    private void UpdateTether()
    {
        if (tetherJoint == null) return;

        // tetherJoint is exclusively for ranged shots holding web.
        // placementRope handles Placement Mode natively on the dynamic body.
        if (lastSegment != null && isHoldingWeb && !isPlacementMode)
        {
            tetherJoint.enabled = true;
            tetherJoint.connectedBody = lastSegment.GetComponent<Rigidbody2D>();
            
            // The tether point on the spider is the current anchor (Head or Foot)
            Vector2 worldAnchorPos = currentTetherAnchor != null ? (Vector2)currentTetherAnchor.position : (Vector2)webStartPoint.position;
            tetherJoint.anchor = transform.InverseTransformPoint(worldAnchorPos);
            tetherJoint.connectedAnchor = Vector2.zero;
        }
        else
        {
            tetherJoint.enabled = false;
        }
    }

    private GameObject SpawnSegment(Vector2 position)
    {
        GameObject webSegmentGO = Instantiate(webPrefab, position, Quaternion.identity, activeWebString.transform);
        WebSegment webSegment = webSegmentGO.GetComponent<WebSegment>();
        
        webSegment.overlapRadius = overlapRadius;
        webSegment.defaultLayer = defaultLayer;
        webSegment.stringId = stringId;
        webSegment.segmentLength = segmentLength;
        
        // Pass the safety info to the segment so it doesn't auto-anchor back to the head
        webSegment.ignoreTransform = transform;
        webSegment.ignoreRadius = safetyRange;

        // 0. Safety: Ignore collision with ALL parts of the spider
        Collider2D[] spiderCols = GetComponentsInChildren<Collider2D>();
        Collider2D segCol = webSegmentGO.GetComponent<Collider2D>();
        if (segCol != null) {
            foreach (var sc in spiderCols) Physics2D.IgnoreCollision(sc, segCol);
        }

        if (currentSegmentCount == 0)
        {
            webSegment.firstSegment = true;
            // Anchor the start to whatever the spider is touching OR the air
            webSegment.CheckContact(); 
        }

        GameObject lastSegmentCopy = lastSegment; // Store the previous segment before reassigning
        lastSegment = webSegmentGO;
        currentSegmentCount++;

        // 3. Stabilization: High damping and low mass prevents the "clanging" vibration
        Rigidbody2D segmentRb = webSegmentGO.GetComponent<Rigidbody2D>();
        if (segmentRb != null)
        {
            segmentRb.mass = 0.1f; // Mass was 50! Reducing this stops the explosion forces
            segmentRb.linearDamping = 0.1f; // Allow the chain to fall quickly
            segmentRb.angularDamping = 0.2f; // Low angular damping allows loose, rope-like bending
            segmentRb.gravityScale = 1.0f; // Normal gravity so it hangs heavily
            segmentRb.collisionDetectionMode = CollisionDetectionMode2D.Discrete; // Use Discrete to save massive CPU physics cost for joined chains
        }

        // 4. Mathematical Joint Precision
        webSegment.hinge.autoConfigureConnectedAnchor = false;
        webSegment.hinge2.autoConfigureConnectedAnchor = false;
        
        // Pivot points at the exact edges of the segment
        webSegment.hinge.anchor = new Vector2(segmentLength * 0.5f, 0);
        webSegment.hinge2.anchor = new Vector2(-segmentLength * 0.5f, 0);

        // FATAL FIX: The newest trailing segment shouldn't be attached to the air in front of it!
        // A null connectedBody on an enabled hinge locks the segment tightly to world space.
        webSegment.hinge.enabled = false;

        if (lastSegmentCopy != null)
        {
            WebSegment prev = lastSegmentCopy.GetComponent<WebSegment>();
            
            // Link them together with ONE joint to prevent physics solver locking (rigidification)
            prev.hinge.enabled = true;
            prev.hinge.connectedBody = segmentRb;
            
            // Match anchors perfectly
            prev.hinge.connectedAnchor = webSegment.hinge2.anchor;

            // Explicitly disable the backwards joint so it doesn't fight the forward joint
            webSegment.hinge2.enabled = false;
            prev.hinge.enableCollision = false;

            // Explicit Physics Ignore
            var currentCol = webSegmentGO.GetComponent<Collider2D>();
            var prevCol = lastSegmentCopy.GetComponent<Collider2D>();
            if (currentCol != null && prevCol != null)
            {
                Physics2D.IgnoreCollision(currentCol, prevCol);
            }

            prev.RegisterNeighbor(webSegment);
            webSegment.RegisterNeighbor(prev);
        }

        return webSegmentGO;
    }

    public void FinalizeExtrusion(Collider2D finalAnchor = null, Vector2 finalPoint = default)
    {
        if (!isHoldingWeb) return;

        // --- Hierarchy Merging (Finalization) ---
        // If we are currently a root string but attaching to a web, merge into that web's folder
        if (activeWebString != null && activeWebString.name.StartsWith("LiveWebString"))
        {
            Collider2D target = finalAnchor;
            if (target == null && lastSegment != null)
            {
                RaycastHit2D hit = Physics2D.Raycast(lastSegment.transform.position, Vector2.zero, 0.5f, LayerMask.GetMask("Web"));
                target = hit.collider;
            }

            if (target != null && target.gameObject.layer == LayerMask.NameToLayer("Web"))
            {
                activeWebString.name = "Branch_" + stringId;
                activeWebString.transform.SetParent(target.transform.parent);
            }
        }
        
        // Physically lock the last segment at the released point
        if (lastSegment != null)
        {
            var ws = lastSegment.GetComponent<WebSegment>();
            if (finalAnchor != null) ws.ForceAttachEnd(finalAnchor, finalPoint);
            else ws.CheckCurrentContact();

            // --- AI NAVIGATION ---
            // Now that the web is finalized, register all segments sparsely for AI navigation.
            // This prevents AI from trying to 'catch' the web while it's still being shot.
            WebSegment[] allSegments = activeWebString.GetComponentsInChildren<WebSegment>();
            for (int i = 0; i < allSegments.Length; i++)
            {
                if (i == 0 || i == allSegments.Length - 1 || i % 8 == 0)
                {
                    var seg = allSegments[i];
                    GlobalAttachmentManager.StoreAttachmentPoint(seg.transform.position, seg.transform.up, seg.transform, 8.0f);
                }
            }
        }

        isHoldingWeb = false;
        lastSegment = null;
        activeWebString = null;
        stringId++;
    }

    // ───────────────────────────────────────────────────────────
    //  Placement Mode (Click-to-Anchor Web Trailing)
    // ───────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to start placement mode by raycasting from the spider toward the
    /// mouse position. If an attachable surface is found within leg reach, the web
    /// is anchored there and the spider begins trailing web from its body.
    /// Returns true if placement mode was entered.
    /// </summary>
    private bool TryBeginPlacement()
    {
        Vector2 mousePos = GetMouseWorldPos();
        Vector2 startPos = (Vector2)webStartPoint.position;
        float distToMouse = Vector2.Distance(startPos, mousePos);

        // Only enter placement mode if the mouse is within leg reach
        if (distToMouse > safetyRange) return false;

        Vector2 direction = (mousePos - startPos).normalized;

        // Raycast toward the mouse to find a surface to anchor to
        RaycastHit2D[] hits = Physics2D.RaycastAll(startPos, direction, safetyRange, defaultLayer | LayerMask.GetMask("Web"));
        RaycastHit2D bestHit = new RaycastHit2D();
        float minD = float.MaxValue;
        int webLayerIdx = LayerMask.NameToLayer("Web");

        foreach (var h in hits)
        {
            if (h.collider == null) continue;
            float d = Vector2.Distance(h.point, startPos);
            // Skip webs that are too close to the spider body (same as ranged shot)
            if (h.collider.gameObject.layer == webLayerIdx && d < safetyRange * 0.3f) continue;
            if (d < minD)
            {
                minD = d;
                bestHit = h;
            }
        }

        if (bestHit.collider == null) return false;

        // ── Start placement ──
        isPlacementMode = true;
        currentSegmentCount = 0;
        lastSegment = null;

        // Create hierarchy container
        if (bestHit.collider.gameObject.layer == webLayerIdx)
        {
            activeWebString = new GameObject("Branch_" + stringId);
            activeWebString.transform.SetParent(bestHit.collider.transform.parent);
        }
        else
        {
            activeWebString = new GameObject("PlacedWebString_" + stringId);
        }
        activeRenderer = activeWebString.AddComponent<WebStringRenderer>();

        // Spawn the first segment at the anchor point, oriented toward the spider
        Vector2 backDir = (startPos - bestHit.point).normalized;
        float angle = Mathf.Atan2(backDir.y, backDir.x) * Mathf.Rad2Deg;

        GameObject firstSeg = SpawnSegment(bestHit.point);
        firstSeg.transform.rotation = Quaternion.Euler(0, 0, angle);
        WebSegment firstWs = firstSeg.GetComponent<WebSegment>();
        firstWs.ForceAttachStart(bestHit.collider, bestHit.point);

        lastExtrusionPos = bestHit.point;

        // Physically attach the trailing end to the spider's Dynamic head via our rope joint.
        // maxDistanceOnly = true means the spider can move freely within segment length
        // but is constrained at max distance (like a rope, not a pin).
        if (placementRope != null)
        {
            Rigidbody2D lastRb = lastSegment.GetComponent<Rigidbody2D>();
            placementRope.connectedBody = lastRb;
            
            // Attach the rope to the spinneret/abdomen rather than the center of mass.
            // This naturally forces gravity to angle the heavy head downwards!
            Transform ropeOrigin = webStartPoint != null ? webStartPoint : transform;
            placementRope.anchor = placementRopeBody.transform.InverseTransformPoint(ropeOrigin.position);
            
            // Attach securely at the segment's logical end
            WebSegment lastWs = lastSegment.GetComponent<WebSegment>();
            placementRope.connectedAnchor = lastWs.hinge.anchor;
            
            placementRope.distance = segmentLength * tensionFactor;
            placementRope.enabled = true;
        }

        // Tell the renderer to draw a trailing line to the spider body's abdomen
        activeRenderer.trailingAnchor = webStartPoint != null ? webStartPoint : transform;

        return true;
    }

    private void UpdatePlacement()
    {
        if (lastSegment == null) return;

        Vector2 anchorPos = currentTetherAnchor != null ? (Vector2)currentTetherAnchor.position : (Vector2)webStartPoint.position;
        Vector2 lastSegPos = lastSegment.transform.position; // Use physical position to prevent infinite unspooling when swinging

        if (IsRappelling)
        {
            // ── Rappelling ──
            // Use vertical input to winch up and down the web
            if (placementRope != null && spiderController != null && Mathf.Abs(spiderController.inputDirection.y) > 0.05f)
            {
                // Down input adds length, Up input pulls in
                placementRope.distance -= spiderController.inputDirection.y * rappellingWinchSpeed * Time.deltaTime;
                placementRope.distance = Mathf.Max(0.1f, placementRope.distance); // Clamp min reel
            }

            // If we've reeled out enough slack, spawn a physical segment to solidify it
            if (placementRope != null && placementRope.distance > segmentLength)
            {
                Vector2 dir = (anchorPos - lastSegPos).normalized;
                GameObject seg = SpawnSegment(anchorPos);
                float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
                seg.transform.rotation = Quaternion.Euler(0, 0, angle);

                Rigidbody2D newRb = lastSegment.GetComponent<Rigidbody2D>();
                placementRope.connectedBody = newRb;
                WebSegment newWs = lastSegment.GetComponent<WebSegment>();
                placementRope.connectedAnchor = newWs.hinge.anchor;
                
                // Consume the slack into the physical segment
                placementRope.distance -= segmentLength;
            }
        }
        else
        {
            // ── Normal Walking Placement ──
            float travelDist = Vector2.Distance(lastSegPos, anchorPos);

            // Trigger at 85% of length. This builds 15% native slack into the chain!
            // This is exactly why shot webs felt silky; they spawned slightly overlapping.
            if (travelDist > segmentLength * tensionFactor * 0.85f)
            {
                Vector2 dir = (anchorPos - lastSegPos).normalized;
                GameObject seg = SpawnSegment(anchorPos);
                float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
                seg.transform.rotation = Quaternion.Euler(0, 0, angle);

                if (placementRope != null)
                {
                    Rigidbody2D newRb = lastSegment.GetComponent<Rigidbody2D>();
                    placementRope.connectedBody = newRb;
                    WebSegment newWs = lastSegment.GetComponent<WebSegment>();
                    placementRope.connectedAnchor = newWs.hinge.anchor;
                    // Provide a constant max leash radius. Because maxDistanceOnly=true,
                    // the spider can freely walk backward without restriction!
                    placementRope.distance = segmentLength * tensionFactor * 0.85f;
                }
            }
        }
    }

    /// <summary>
    /// Finalizes placement mode: raycasts from the spider toward the mouse
    /// to find a surface, then anchors the trailing end there.
    /// </summary>
    private void TryFinalizePlacement()
    {
        if (!isPlacementMode) return;

        Vector2 mousePos = GetMouseWorldPos();
        Vector2 startPos = (Vector2)webStartPoint.position;
        Vector2 direction = (mousePos - startPos).normalized;

        // Raycast toward mouse to find an anchor surface
        RaycastHit2D[] hits = Physics2D.RaycastAll(startPos, direction, safetyRange, defaultLayer | LayerMask.GetMask("Web"));
        RaycastHit2D bestHit = new RaycastHit2D();
        float minD = float.MaxValue;
        int webLayerIdx = LayerMask.NameToLayer("Web");

        foreach (var h in hits)
        {
            if (h.collider == null) continue;
            float d = Vector2.Distance(h.point, startPos);
            if (h.collider.gameObject.layer == webLayerIdx && d < safetyRange * 0.3f) continue;
            if (d < minD)
            {
                minD = d;
                bestHit = h;
            }
        }

        if (bestHit.collider == null) return; // No surface found — keep trailing

        // Hierarchy merging: if finalizing onto a web, merge into its folder
        if (activeWebString != null && activeWebString.name.StartsWith("PlacedWebString"))
        {
            if (bestHit.collider.gameObject.layer == webLayerIdx)
            {
                activeWebString.name = "Branch_" + stringId;
                activeWebString.transform.SetParent(bestHit.collider.transform.parent);
            }
        }

        // Anchor the trailing end to the target surface
        if (lastSegment != null)
        {
            var ws = lastSegment.GetComponent<WebSegment>();
            ws.ForceAttachEnd(bestHit.collider, bestHit.point);

            // Register for AI navigation
            WebSegment[] allSegments = activeWebString.GetComponentsInChildren<WebSegment>();
            for (int i = 0; i < allSegments.Length; i++)
            {
                if (i == 0 || i == allSegments.Length - 1 || i % 8 == 0)
                {
                    var seg = allSegments[i];
                    GlobalAttachmentManager.StoreAttachmentPoint(seg.transform.position, seg.transform.up, seg.transform, 8.0f);
                }
            }
        }

        // Disable physical connections and clean up
        if (tetherJoint != null) tetherJoint.enabled = false;
        if (placementRope != null) placementRope.enabled = false;

        // Clear the trailing visual anchor so the renderer stops drawing to the spider
        if (activeRenderer != null)
        {
            activeRenderer.trailingAnchor = null;
            activeRenderer = null;
        }

        isPlacementMode = false;
        lastSegment = null;
        activeWebString = null;
        stringId++;
    }

    public Vector2 GetLastSegmentPos() => lastSegment != null ? (Vector2)lastSegment.transform.position : (Vector2)webStartPoint.position;

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
}

