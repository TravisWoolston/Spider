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
    [Range(1f, 1.2f)] public float tensionFactor = 1.01f; // Low = Tight, High = Slack
    private float safetyRange;
    private DistanceJoint2D tetherJoint;
    private Transform currentTetherAnchor;

    private GameObject activeWebString;
    private GameObject lastSegment;
    private bool isHoldingWeb = false;
    private int currentSegmentCount = 0;
    private Vector2 lastExtrusionPos;
    public bool IsHoldingWeb => isHoldingWeb;

    void Awake()
    {
        // Initialize tethering physics immediately so other scripts can access it
        tetherJoint = gameObject.AddComponent<DistanceJoint2D>();
        tetherJoint.enabled = false;
        tetherJoint.autoConfigureDistance = false;
        tetherJoint.distance = 0.1f;
        tetherJoint.maxDistanceOnly = false; // Keep it tight
        currentTetherAnchor = webStartPoint != null ? webStartPoint : transform;
    }

    void Start()
    {
        if (webRb != null) segmentLength = webRb.size.x; // size.x is the FULL width
        var controller = GetComponent<SpiderControllerIK>();
        // Using LegExtensionMax as our safety range as requested
        if (controller != null) safetyRange = controller.legExtensionMax;
    }

    void Update()
    {
        if (Input.GetMouseButtonDown(1)) // Right Click to shoot/extrude (as requested)
        {
            if (!isHoldingWeb) StartCoroutine(BeginExtrusion());
            else FinalizeExtrusion();
        }

        if (isHoldingWeb)
        {
            UpdateExtrusion();
        }
    }

    private IEnumerator BeginExtrusion()
    {
        isHoldingWeb = true;
        currentSegmentCount = 0;
        lastSegment = null; // Clean slate
        Vector2 mousePos = Camera.main.ScreenToWorldPoint(Input.mousePosition);
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
                else ws.CheckCurrentContact(); // Miss case: try to anchor to whatever is there
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

        if (lastSegment != null && isHoldingWeb)
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
            segmentRb.linearDamping = 5f;
            segmentRb.angularDamping = 5f;
            segmentRb.gravityScale = 0.5f; // Reduce sag tension
            segmentRb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        }

        // 4. Mathematical Joint Precision
        webSegment.hinge.autoConfigureConnectedAnchor = false;
        webSegment.hinge2.autoConfigureConnectedAnchor = false;
        
        // Pivot points at the exact edges of the segment
        webSegment.hinge.anchor = new Vector2(segmentLength * 0.5f, 0);
        webSegment.hinge2.anchor = new Vector2(-segmentLength * 0.5f, 0);

        if (lastSegmentCopy != null)
        {
            WebSegment prev = lastSegmentCopy.GetComponent<WebSegment>();
            
            // Link them together
            prev.hinge.connectedBody = segmentRb;
            webSegment.hinge2.connectedBody = lastSegmentCopy.GetComponent<Rigidbody2D>();
            
            // Match anchors perfectly
            prev.hinge.connectedAnchor = webSegment.hinge2.anchor;
            webSegment.hinge2.connectedAnchor = prev.hinge.anchor;

            prev.hinge.enableCollision = false;
            webSegment.hinge2.enableCollision = false;

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
        }

        isHoldingWeb = false;
        lastSegment = null;
        activeWebString = null;
        stringId++;
    }

    public Vector2 GetLastSegmentPos() => lastSegment != null ? (Vector2)lastSegment.transform.position : (Vector2)webStartPoint.position;
}

