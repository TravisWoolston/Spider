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
    public float maxLightIntensity = 50.0f; // Increased for better visibility
    public float lightOuterRadius = 1000.0f;  // Increased radius
    public Color pulseColor = Color.white; // Neutral white light

    void Start()
    {
        rb = GetComponent<Rigidbody2D>();
        // hinge = GetComponent<HingeJoint2D>();
        spriteRenderer = GetComponent<SpriteRenderer>();
        if (spriteRenderer != null)
        {
            originalColor = spriteRenderer.color;
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
            pulseLight.pointLightOuterRadius = lightOuterRadius;
            pulseLight.color = pulseColor;
            pulseLight.shadowsEnabled = false; // Usually better for performance and visibility here
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
        // Debug.Log($"[WebSense] Pulse triggered on segment {gameObject.name} (String ID: {stringId})");
        currentPulseId++;
        Pulse(1.0f, currentPulseId);
    }

    public void Pulse(float strength, int pulseId)
    {
        if (lastPulseId == pulseId || strength <= 0.01f)
        {
            return;
        }
        lastPulseId = pulseId;

        StopAllCoroutines();
        StartCoroutine(PulseRoutine(strength, pulseId));
    }

    private IEnumerator PulseRoutine(float strength, int pulseId)
    {
        float duration = 0.6f; // Slightly longer for better visibility
        float elapsed = 0;
        
        // Propagate pulse to neighbors after a small delay
        float propagationDelay = 0.04f;
        // Start showing local pulse immediately
        
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / duration;
            if (spriteRenderer != null)
            {
                spriteRenderer.color = Color.Lerp(pulseColor, originalColor, t);
            }

            if (pulseLight != null)
            {
                pulseLight.intensity = Mathf.Lerp(maxLightIntensity * strength, 0, t);
            }

            if (elapsed >= propagationDelay && propagationDelay > 0)
            {
                // Propagate once
                foreach (var neighbor in neighbors)
                {
                    if (neighbor != null)
                    {
                        neighbor.Pulse(strength * 0.96f, pulseId); // Slower decay
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
public void CheckContact()
{
    // Define the direction of the raycast from hinge2 to the other end (hinge)
    Vector2 rayDirection = -transform.right;  // This assumes the object is facing along its 'right' axis

    // Perform the raycast from the position of hinge2 in the direction of the target
    RaycastHit2D[] hits = Physics2D.RaycastAll(hinge2.transform.position, rayDirection, overlapRadius, defaultLayer | LayerMask.GetMask("Web"));
    
    RaycastHit2D closestHit = new RaycastHit2D();
    float closestDistance = float.MaxValue;

    foreach (RaycastHit2D hit in hits)
    {
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
        RaycastHit2D[] webHits = Physics2D.RaycastAll(
            hinge.transform.position,
            transform.right,
            overlapRadius,
            LayerMask.GetMask("Web")
        );
        foreach(RaycastHit2D webHit in webHits){

        
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
            }

            return true;
        }
        }
        return false; // Continue creating segments
    }
}
