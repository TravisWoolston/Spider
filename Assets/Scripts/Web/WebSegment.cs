using UnityEngine;

public class WebSegment : MonoBehaviour
{
    public bool isAttached = false;
    private Rigidbody2D rb;
    public HingeJoint2D hinge;
    public HingeJoint2D hinge2;
    public bool firstSegment = false;
    public float overlapRadius = .2f;
    public LayerMask defaultLayer;
    public int stringId = -1;
    public float segmentLength;

    void Start()
    {
        rb = GetComponent<Rigidbody2D>();
        // hinge = GetComponent<HingeJoint2D>();
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
        hinge2.connectedBody = closestHit.collider.GetComponent<Rigidbody2D>();
        hinge2.connectedAnchor = closestHit.collider.transform.InverseTransformPoint(closestHit.point);
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
            // Attach the segment to the hit object
            hinge.connectedBody = hit.collider.GetComponent<Rigidbody2D>();
            // hinge.anchor = new Vector2(0, -segmentLength / 2);
            hinge.connectedAnchor = transform.InverseTransformPoint(
                new Vector2(segmentLength / 2, 0)
            );

            isAttached = true;
            Debug.Log("Web segment attached to: " + hit.collider.name); // Debugging info
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
            return true;
        }
        }
        return false; // Continue creating segments
    }
}
