using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.Rendering.Universal;

public class WebSegment : MonoBehaviour
{
    public bool isAttached = false;
    private Rigidbody2D rb;
    public HingeJoint2D hinge;
    public HingeJoint2D hinge2;
    public bool firstSegment = false;
    [HideInInspector] public float overlapRadius = .2f;
    public LayerMask defaultLayer;
    public int stringId = -1;
    [HideInInspector] public float segmentLength;
    [Header("Safety")]
    public Transform ignoreTransform;
    public float ignoreRadius = 0f;

    public void ForceAttachStart(Collider2D col, Vector2 point)
    {
        if (hinge2 == null) return;
        var targetRb = col.GetComponentInParent<Rigidbody2D>(); // Parent in case of child colliders
        hinge2.connectedBody = targetRb;
        hinge2.connectedAnchor = targetRb != null ? (Vector2)targetRb.transform.InverseTransformPoint(point) : point;
        hinge2.enabled = true;
        isAttached = true;

        if (col.gameObject.layer == LayerMask.NameToLayer("Web"))
        {
            WebSegment other = col.GetComponent<WebSegment>();
            if (other != null) {
                // Snap to nearest pivot of the target web segment
                Vector2 localPoint = other.transform.InverseTransformPoint(point);
                float dStart = Vector2.Distance(localPoint, other.hinge2.anchor);
                float dEnd = Vector2.Distance(localPoint, other.hinge.anchor);
                hinge2.connectedAnchor = (dStart < dEnd) ? other.hinge2.anchor : other.hinge.anchor;

                RegisterNeighbor(other);
                other.RegisterNeighbor(this);
            }
        }
    }

    public void ForceAttachEnd(Collider2D col, Vector2 point)
    {
        if (hinge == null) return;
        var targetRb = col.GetComponentInParent<Rigidbody2D>(); // Parent in case of child colliders
        hinge.connectedBody = targetRb;
        hinge.connectedAnchor = targetRb != null ? (Vector2)targetRb.transform.InverseTransformPoint(point) : point;
        hinge.enabled = true;
        isAttached = true;

        if (col.gameObject.layer == LayerMask.NameToLayer("Web"))
        {
            WebSegment other = col.GetComponent<WebSegment>();
            if (other != null) {
                // Snap to nearest pivot of the target web segment
                Vector2 localPoint = other.transform.InverseTransformPoint(point);
                float dStart = Vector2.Distance(localPoint, other.hinge2.anchor);
                float dEnd = Vector2.Distance(localPoint, other.hinge.anchor);
                hinge.connectedAnchor = (dStart < dEnd) ? other.hinge2.anchor : other.hinge.anchor;

                RegisterNeighbor(other);
                other.RegisterNeighbor(this);
            }
        }
    }

    public void CheckCurrentContact()
    {
        // For final release, we use the end hinge
        RaycastHit2D hit = Physics2D.Raycast(transform.position, Vector2.zero, 0.5f, defaultLayer | LayerMask.GetMask("Web"));
        if (hit.collider != null) ForceAttachEnd(hit.collider, hit.point);
    }

    private SpriteRenderer spriteRenderer;
    private Color originalColor;
    private static int currentPulseId = 0;
    private int lastPulseId = -1;
    public List<WebSegment> neighbors = new List<WebSegment>();

    private Light2D pulseLight;
    public float maxLightIntensity = 20.0f; // Lowered to prevent yellowish saturation
    public float lightOuterRadius = 1000.0f;  // Increased radius
    public float lightInnerRadius = 0.0f;    // Where the light starts to dim
    public float lightFalloff = 0.5f;        // 0 = Sharp, 1 = Very gradual/soft
    public float pulseDuration = 0.6f;      // How long the light takes to fade out
    public float pulseAttackDuration = 0.1f; // How long the light takes to reach peak brightness
    public Color pulseColor = Color.white;  // Pulse color for the sprite
    public float spatialPulseRadius = 15f;  // Radius for immediate spatial glow

    void Start()
    {
        rb = GetComponent<Rigidbody2D>();
        // hinge = GetComponent<HingeJoint2D>();
        spriteRenderer = GetComponent<SpriteRenderer>();
        
        // Force neutral white colors to avoid yellowish tints from the inspector
        pulseColor = Color.white;
        
        // Aggressively force neutral white to override any yellow inspector settings
        pulseColor = Color.white;
        
        if (spriteRenderer != null)
        {
            // If the sprite itself is yellowish, we force it to white to ensure the pulse is clean
            spriteRenderer.color = Color.white;
            originalColor = Color.white;
        }

        pulseLight = GetComponent<Light2D>();
        if (pulseLight == null)
        {
            pulseLight = gameObject.AddComponent<Light2D>();
            pulseLight.lightType = Light2D.LightType.Point;
        }

        if (pulseLight != null)
        {
            pulseLight.intensity = 0;
            pulseLight.pointLightInnerRadius = lightInnerRadius;
            pulseLight.pointLightOuterRadius = lightOuterRadius;
            pulseLight.falloffIntensity = lightFalloff;
            pulseLight.color = Color.white;
            pulseLight.shadowsEnabled = false; 
        }
    }

    public void RegisterNeighbor(WebSegment other)
    {
        if (other != null && !neighbors.Contains(other))
        {
            neighbors.Add(other);
        }
    }

    public void TriggerPulse()
    {
        currentPulseId++;
        Vector2 sourcePos = (Vector2)transform.position;

        // Perform a spatial query to find all web segments in range
        Collider2D[] webColliders = Physics2D.OverlapCircleAll(sourcePos, spatialPulseRadius, LayerMask.GetMask("Web"));
        
        foreach (var col in webColliders)
        {
            var segment = col.GetComponent<WebSegment>();
            if (segment != null)
            {
                float dist = Vector2.Distance(sourcePos, (Vector2)segment.transform.position);
                float strength = Mathf.Clamp01(1f - (dist / spatialPulseRadius));
                
                // Use a non-linear falloff for a more natural look
                strength = Mathf.Pow(strength, 1.2f);
                
                // Pulse immediately without propagation (since we've already found everyone spatially)
                segment.Pulse(strength, currentPulseId, false);
            }
        }
    }

    public void Pulse(float strength, int pulseId, bool propagate = true)
    {
        if (lastPulseId == pulseId || strength <= 0.01f)
        {
            return;
        }
        lastPulseId = pulseId;

        // Capture current state to lerp from instead of snapping
        float startInt = (pulseLight != null) ? pulseLight.intensity / maxLightIntensity : 0f;

        StopAllCoroutines();
        StartCoroutine(PulseRoutine(strength, pulseId, propagate, startInt));
    }

    private IEnumerator PulseRoutine(float targetStrength, int pulseId, bool propagate, float startStrength)
    {
        float duration = pulseDuration;
        float attack = pulseAttackDuration;
        float elapsed = 0;
        
        // Propagation settings (ignored if propagate is false)
        float propagationDelay = 0.04f;
        
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float currentStrength;

            if (elapsed < attack)
            {
                // Ramp up from wherever we were to the new peak
                float t = elapsed / attack;
                currentStrength = Mathf.Lerp(startStrength, targetStrength, t);
            }
            else
            {
                // Fade out normally
                float t = (elapsed - attack) / (duration - attack);
                currentStrength = Mathf.Lerp(targetStrength, 0, t);
            }

            if (spriteRenderer != null)
            {
                spriteRenderer.color = Color.Lerp(originalColor, pulseColor, currentStrength);
            }

            if (pulseLight != null)
            {
                pulseLight.color = Color.white;
                pulseLight.intensity = maxLightIntensity * currentStrength;
            }

            if (propagate && elapsed >= propagationDelay && propagationDelay > 0)
            {
                // Propagate once to neighbors
                foreach (var neighbor in neighbors)
                {
                    if (neighbor != null)
                    {
                        neighbor.Pulse(targetStrength * 0.96f, pulseId, true);
                    }
                }
                propagationDelay = -1f; 
            }
            yield return null;
        }
        
        if (spriteRenderer != null)
        {
            spriteRenderer.color = originalColor;
        }

        if (pulseLight != null)
        {
            pulseLight.intensity = 0;
        }
    }

    void Update()
    {
        if (!firstSegment)
        {
            return;
        }
        CheckContact();
    }

    // public void CheckContact()
    // {
    //     Collider2D overlapCollider = Physics2D.OverlapCircle(
    //         transform.position,
    //         overlapRadius,
    //         defaultLayer
    //     );
    //     if (overlapCollider != null)
    //     {
    //         hinge2.connectedBody = overlapCollider.GetComponent<Rigidbody2D>();
    //         hinge2.connectedAnchor = overlapCollider.transform.InverseTransformPoint(
    //             transform.position
    //         );
    //         hinge2.enabled = true;
    //         firstSegment = false;
    //         isAttached = true;
    //     }
    // }
    private static RaycastHit2D[] contactHitsBuffer = new RaycastHit2D[50]; // Static buffer for zero-alloc raycasting

    public void CheckContact()
    {
        // Define the direction of the raycast from hinge2 to the other end (hinge)
        Vector2 rayDirection = -transform.right;  // This assumes the object is facing along its 'right' axis

        // Perform the raycast from the position of hinge2 in the direction of the target
        int hitCount = Physics2D.RaycastNonAlloc(hinge2.transform.position, rayDirection, contactHitsBuffer, overlapRadius, defaultLayer | LayerMask.GetMask("Web"));
        
        RaycastHit2D closestHit = new RaycastHit2D();
        float closestDistance = float.MaxValue;

        for (int i = 0; i < hitCount; i++)
        {
            RaycastHit2D hit = contactHitsBuffer[i];
            if (hit.collider != null)
        {
            // Safety Check: Don't attach to things too close to the spider's head during firing
            if (ignoreTransform != null && Vector2.Distance(hit.point, ignoreTransform.position) < ignoreRadius)
                continue;

            bool isValidHit = false;

            // Check if hit is on the "Web" layer and not part of the same string
            if (hit.collider.gameObject.layer == LayerMask.NameToLayer("Web"))
            {
                WebSegment webSegment = hit.collider.GetComponent<WebSegment>();
                if (webSegment != null && webSegment.stringId != stringId)
                {
                    isValidHit = true;
                }
            }
            else
            {
                // If not on the "Web" layer, it's a valid default layer hit
                isValidHit = true;
            }

            // If it's a valid hit, check if it's the closest hit
            if (isValidHit)
            {
                float distance = Vector2.Distance(hinge2.transform.position, hit.point);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestHit = hit;
                }
            }
        }
    }

    // If a valid closest hit was found, attach hinge2 to it
    if (closestHit.collider != null)
    {
        hinge2.connectedBody = closestHit.collider.GetComponentInParent<Rigidbody2D>();
        
        WebSegment other = closestHit.collider.GetComponent<WebSegment>();
        if (other != null)
        {
            // Snap to nearest pivot of the target web segment
            Vector2 localPoint = other.transform.InverseTransformPoint(closestHit.point);
            float dStart = Vector2.Distance(localPoint, other.hinge2.anchor);
            float dEnd = Vector2.Distance(localPoint, other.hinge.anchor);
            hinge2.connectedAnchor = (dStart < dEnd) ? other.hinge2.anchor : other.hinge.anchor;

            RegisterNeighbor(other);
            other.RegisterNeighbor(this);

            var myCol = GetComponent<Collider2D>();
            var otherCol = other.GetComponent<Collider2D>();
            if (myCol != null && otherCol != null)
            {
                Physics2D.IgnoreCollision(myCol, otherCol);
            }
        }
        else
        {
            hinge2.connectedAnchor = closestHit.collider.transform.InverseTransformPoint(closestHit.point);
        }

        hinge2.enabled = true;
        firstSegment = false;
        isAttached = true;
    }
}

    void OnDrawGizmos()
    {
        if (hinge2 != null)
        {
            // Define the direction of the raycast from hinge2 to the other end (hinge)
            Vector2 rayDirection = -transform.right;  // This assumes the object is facing along its 'right' axis

            // Define the starting position of the raycast
            Vector3 rayOrigin = hinge2.transform.position;

            // Define the end position of the raycast
            Vector3 rayEnd = rayOrigin + (Vector3)rayDirection * overlapRadius;

            // Draw the raycast as a red line
            Gizmos.color = Color.red;
            // Gizmos.DrawLine(rayOrigin, rayEnd);

            // Optionally, draw a small sphere at the hit point (if it exists)
            Gizmos.DrawSphere(rayEnd, 0.05f);

                   rayDirection = transform.right;  // This assumes the object is facing along its 'right' axis

            // Define the starting position of the raycast
            rayOrigin = hinge2.transform.position;

            // Define the end position of the raycast
            rayEnd = rayOrigin + (Vector3)rayDirection * overlapRadius;

            // Draw the raycast as a red line
            Gizmos.color = Color.blue;
            Gizmos.DrawLine(rayOrigin, rayEnd);

            // Optionally, draw a small sphere at the hit point (if it exists)
            Gizmos.DrawSphere(rayEnd, 0.05f);
        }
    }
    public void AttachToSurface(Collision2D collision)
    {
        if (
            !isAttached
            && (
                collision.gameObject.layer == LayerMask.NameToLayer("Default")
                || collision.gameObject.layer == LayerMask.NameToLayer("Web")
            )
        )
        {
            isAttached = true;

            // Attach to the collided object
            hinge.connectedBody = collision.rigidbody;
            hinge.anchor = Vector2.zero;
            hinge.connectedAnchor =
                collision.contacts[0].point - (Vector2)collision.transform.position;

            // Optionally, disable gravity and freeze the rotation to simulate a "stick" effect
            rb.gravityScale = 0;
            rb.freezeRotation = true;
        }
    }

    public bool CheckForSurfaceCollision(Vector2 direction)
    {
        RaycastHit2D hit = Physics2D.Raycast(
            hinge.transform.position,
            transform.right,
            overlapRadius,
            LayerMask.GetMask("Default")
        );

        if (hit.collider != null)
        {
            if (ignoreTransform != null && Vector2.Distance(hit.point, ignoreTransform.position) < ignoreRadius)
                return false;

            // Attach the segment to the hit object
            hinge.connectedBody = hit.collider.GetComponent<Rigidbody2D>();
            // hinge.anchor = new Vector2(0, -segmentLength / 2);
            hinge.connectedAnchor = transform.InverseTransformPoint(
                new Vector2(segmentLength / 2, 0)
            );

            isAttached = true;
            Debug.Log("Web segment attached to: " + hit.collider.name); // Debugging info

            WebSegment other = hit.collider.GetComponent<WebSegment>();
            if (other != null)
            {
                RegisterNeighbor(other);
                other.RegisterNeighbor(this);
            }

            return true; // Stop creating more segments
        }
        int hitCount = Physics2D.RaycastNonAlloc(
            hinge.transform.position,
            transform.right,
            contactHitsBuffer,
            overlapRadius,
            LayerMask.GetMask("Web")
        );
        for(int i = 0; i < hitCount; i++){
            RaycastHit2D webHit = contactHitsBuffer[i];
        
        if (webHit.collider != null)
        {
            if (ignoreTransform != null && Vector2.Distance(webHit.point, ignoreTransform.position) < ignoreRadius)
                continue;

            if (webHit.collider.GetComponent<WebSegment>().stringId == stringId)
            {
                continue;
            }
            hinge.connectedBody = webHit.collider.GetComponent<Rigidbody2D>();
            // hinge.anchor = new Vector2(0, -segmentLength / 2);
            hinge.connectedAnchor = transform.InverseTransformPoint(
                new Vector2(segmentLength / 2, 0)
            );
            isAttached = true;
            Debug.Log(
                "Web Id "
                    + stringId
                    + " segment attached to: "
                    + webHit.collider.name
                    + " "
                    + webHit.collider.GetComponent<WebSegment>().stringId
            ); // Debugging info

            WebSegment other = webHit.collider.GetComponent<WebSegment>();
            if (other != null)
            {
                RegisterNeighbor(other);
                other.RegisterNeighbor(this);
                
                var myCol = GetComponent<Collider2D>();
                var otherCol = other.GetComponent<Collider2D>();
                if (myCol != null && otherCol != null)
                {
                    Physics2D.IgnoreCollision(myCol, otherCol);
                }
            }

            return true;
        }
        }
        return false; // Continue creating segments
    }
}
