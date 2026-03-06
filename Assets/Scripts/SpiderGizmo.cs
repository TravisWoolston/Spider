using UnityEngine;
using System.Collections.Generic;

[ExecuteInEditMode]
public class SpiderGizmo : MonoBehaviour
{
    public SpiderController spiderController;
    public Color headColor = Color.red;
    public Color leftLegColor = Color.green;
    public Color rightLegColor = Color.yellow;
    public Color footColor = Color.blue;
    public Color raycastColorMiss = Color.magenta;
    public Color raycastColorHit = Color.cyan;
    public Color leftAttachmentPointColor = Color.blue;
    public Color rightAttachmentPointColor = Color.cyan;
    public Color attachPointConsideredColor = Color.yellow;
    public Color currentTargetColor = Color.green;
    public Color moveToTargetColor = Color.white;
    public Color attachedColor = Color.red;
    public Color attemptedAngleColor = Color.grey;
    public float gizmoSize = 0.1f;
    public bool use;
    public bool useRaycasts;
    public float arrowLength = 2.0f; // Length of the arrow
    public Color arrowColor = Color.red; // Color of the arrow

    void OnDrawGizmos()
    {
        if (!use)
            return;

        Gizmos.color = headColor;
        Gizmos.DrawSphere(spiderController.head.position, gizmoSize);
        Vector2 inputDirection = spiderController.inputDirection;
        Gizmos.color = arrowColor;

        // Determine the start and end points for the arrow
        Vector3 startPoint = spiderController.head.transform.position;
        Vector3 endPoint = startPoint + (Vector3)inputDirection.normalized * arrowLength;

        // Draw the main line of the arrow
        Gizmos.DrawLine(startPoint, endPoint);

        // Draw the arrowhead
        DrawArrowHead(endPoint, inputDirection);

        foreach (SpiderLeg leg in spiderController.legs)
        {
            if (leg != null)
            {
                Gizmos.color = arrowColor;
                Vector2 localInputDirection = spiderController.head.InverseTransformDirection(
                    inputDirection
                );
                localInputDirection.Normalize();
                inputDirection = spiderController.head.TransformDirection(localInputDirection);

                // Determine the start and end points for the arrow
                startPoint = leg.legSegment2.transform.position;
                endPoint = startPoint + (Vector3)inputDirection * arrowLength;

                // Draw the main line of the arrow
                Gizmos.DrawLine(startPoint, endPoint);

                // Draw the arrowhead
                DrawArrowHead(endPoint, inputDirection);
                // Color the legs based on their side
                Gizmos.color = leg.isLeftSide ? leftLegColor : rightLegColor;
                Gizmos.DrawLine(leg.legSegment1.position, leg.legSegment2.position);

                Gizmos.color = footColor;
                Vector2 footPosition = leg.GetFootPosition();
                Gizmos.DrawSphere(footPosition, gizmoSize);
                // Gizmos.DrawSphere(leg.legSegment1.transform.TransformPoint(leg.hingeSegment2.connectedAnchor), 1);
                // Draw line from leg foot to the current target
                Gizmos.color = leg.isAttached ? attachedColor : currentTargetColor;
                Gizmos.DrawLine(footPosition, leg.currentTarget);

                // Visualize the movement direction of the leg
                Gizmos.color = moveToTargetColor;
                Vector3 moveToTargetDirection =
                    (Vector3)leg.legSegment2.position + (Vector3)(leg.legSegment2.right * 2f);
                Gizmos.DrawLine(leg.legSegment2.position, moveToTargetDirection);

                // Visualize the raycast from the leg to detect surfaces
                Vector2 raycastDirection = leg.legSegment2.right;
                RaycastHit2D hit = Physics2D.Raycast(
                    footPosition,
                    raycastDirection,
                    spiderController.legSightDistance,
                    leg.attachableLayers
                );

                if (hit.collider != null)
                {
                    Gizmos.color = raycastColorHit;
                    Gizmos.DrawLine(footPosition, hit.point);
                }
                else
                {
                    Gizmos.color = raycastColorMiss;
                    Vector3 raycastEnd =
                        (Vector3)footPosition
                        + (Vector3)(raycastDirection * spiderController.legSightDistance);
                    Gizmos.DrawLine(footPosition, raycastEnd);
                }

                // Visualize the attempted angles for each leg segment
                Gizmos.color = attemptedAngleColor;
                Vector2 targetDirection =
                    leg.inputDirection != Vector2.zero
                        ? leg.inputDirection
                        : leg.currentTarget - (Vector2)leg.legSegment2.position;
                float angle1 = Vector2.SignedAngle(leg.legSegment1.right, targetDirection);
                float angle2 = Vector2.SignedAngle(leg.legSegment2.right, targetDirection);

                Vector3 segment1End =
                    (Vector3)leg.legSegment1.position + (Vector3)(leg.legSegment1.right * 2f);
                Vector3 segment2End =
                    (Vector3)leg.legSegment2.position + (Vector3)(leg.legSegment2.right * 2f);

                Gizmos.DrawLine(leg.legSegment1.position, segment1End);
                Gizmos.DrawLine(leg.legSegment2.position, segment2End);

                // Visualize the angles being attempted
                Vector3 attemptedDirection1 =
                    (Vector3)leg.legSegment1.position
                    + (Vector3)(Quaternion.Euler(0, 0, angle1) * Vector2.right * 2f);
                Vector3 attemptedDirection2 =
                    (Vector3)leg.legSegment2.position
                    + (Vector3)(Quaternion.Euler(0, 0, angle2) * Vector2.right * 2f);

                Gizmos.DrawLine(leg.legSegment1.position, attemptedDirection1);
                Gizmos.DrawLine(leg.legSegment2.position, attemptedDirection2);

                // Draw foot attachment points
                Gizmos.color = attachPointConsideredColor;
                foreach (var point in leg.footAttachmentPoints)
                {
                    Gizmos.DrawSphere(point.point, gizmoSize);
                }
            }
        }

        // Draw left attachment points
        if(useRaycasts){
        Gizmos.color = leftAttachmentPointColor;
        foreach (var point in spiderController.leftAttachmentPoints)
        {
            Gizmos.DrawSphere(point.point, gizmoSize);
            Gizmos.DrawLine(point.point, spiderController.head.position);
        }

        // Draw right attachment points
        Gizmos.color = rightAttachmentPointColor;
        foreach (var point in spiderController.rightAttachmentPoints)
        {
            Gizmos.DrawSphere(point.point, gizmoSize);
            Gizmos.DrawLine(point.point, spiderController.head.position);
        }
        }


        // Highlight points being considered by legs
        foreach (SpiderLeg leg in spiderController.legs)
        {
            if (leg != null && leg.currentTarget != Vector2.zero)
            {
                Gizmos.color = attachPointConsideredColor;
                Gizmos.DrawSphere(leg.currentTarget, gizmoSize * 1.2f);
            }
        }
    }

    void DrawArrowHead(Vector3 arrowTip, Vector2 direction)
    {
        float arrowHeadAngle = 20.0f; // Angle for the arrowhead lines
        float arrowHeadLength = 0.5f; // Length of the arrowhead lines

        Vector3 right = Quaternion.Euler(0, 0, arrowHeadAngle) * (-direction.normalized);
        Vector3 left = Quaternion.Euler(0, 0, -arrowHeadAngle) * (-direction.normalized);

        Gizmos.DrawLine(arrowTip, arrowTip + right * arrowHeadLength);
        Gizmos.DrawLine(arrowTip, arrowTip + left * arrowHeadLength);
    }
}
