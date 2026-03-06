using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class versionstorage : MonoBehaviour
{
    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}


// public class SpiderLeg : MonoBehaviour
// {
//     public Transform legSegment1;
//     public Transform legSegment2;
//     public Transform head;
//     public bool isLeftSide;  // Indicates if the leg is on the left side of the spider

//     private float motorSpeed;
//     private float maxMotorTorque;
//     private float attachDistance;
//     private LayerMask attachableLayers;
//     private HingeJoint2D hingeSegment1;
//     private HingeJoint2D hingeSegment2;
//     private HingeJoint2D footHinge;
//     private bool isAttached = false;
//     private Vector2 inputDirection;
//     private List<Vector2> attachmentPoints;
//     private Vector2 currentTarget;

//     public void InitializeLeg(float motorSpeed, float maxMotorTorque, float attachDistance, LayerMask attachableLayers)
//     {
//         this.motorSpeed = motorSpeed;
//         this.maxMotorTorque = maxMotorTorque;
//         this.attachDistance = attachDistance;
//         this.attachableLayers = attachableLayers;

//         hingeSegment1 = legSegment1.GetComponent<HingeJoint2D>();
//         HingeJoint2D[] hinges = legSegment2.GetComponents<HingeJoint2D>();
//         hingeSegment2 = hinges[0];
//         footHinge = hinges[1];
//         footHinge.enabled = false;
//     }

//     void FixedUpdate()
//     {
//         if (isAttached)
//         {
//             if (inputDirection == Vector2.zero)
//             {
//                 LockLeg();
//             }
//             else
//             {
//                 MoveLeg();
//             }
//         }
//         else
//         {
//             TryAttach();
//             if (!isAttached)
//             {
//                 MoveLeg(0.25f); // Move at quarter speed if not attached
//             }
//             else
//             {
//                 MoveLeg();
//             }
//         }
//         TryDetach();
//     }

//     void TryAttach()
//     {
//         if (!isAttached && attachmentPoints != null)
//         {
//             Vector2 nearestPoint = Vector2.zero;
//             float minDistance = float.MaxValue;

//             foreach (var point in attachmentPoints)
//             {
//                 float distance = Vector2.Distance(GetFootPosition(), point);
//                 if (distance < minDistance)
//                 {
//                     minDistance = distance;
//                     nearestPoint = point;
//                 }
//             }

//             if (minDistance < attachDistance)
//             {
//                 ActivateFootHinge(nearestPoint);
//                 currentTarget = nearestPoint;
//                 attachmentPoints.Remove(nearestPoint); // Ensure the point is unique
//             }
//         }
//     }

//     void MoveLeg(float speedMultiplier = 1.0f)
//     {
//         Vector2 targetDirection = inputDirection != Vector2.zero ? inputDirection : currentTarget - (Vector2)legSegment2.position;

//         float targetAngle1 = Vector2.SignedAngle(legSegment1.right, targetDirection);
//         float targetAngle2 = Vector2.SignedAngle(legSegment2.right, targetDirection);

//         if (CanContributeToMovement(targetAngle1, targetAngle2))
//         {
//             SetMotorSpeed(hingeSegment1, targetAngle1 * motorSpeed * speedMultiplier);
//             SetMotorSpeed(hingeSegment2, targetAngle2 * motorSpeed * speedMultiplier);
//         }
//         else
//         {
//             FindNewTarget();
//         }
//     }

//     void LockLeg()
//     {
//         // Set motor speed to zero to lock the leg in place
//         SetMotorSpeed(hingeSegment1, 0);
//         SetMotorSpeed(hingeSegment2, 0);
//     }

//     bool CanContributeToMovement(float angle1, float angle2)
//     {
//         float lowerLimit1 = hingeSegment1.limits.min;
//         float upperLimit1 = hingeSegment1.limits.max;
//         float lowerLimit2 = hingeSegment2.limits.min;
//         float upperLimit2 = hingeSegment2.limits.max;

//         return (angle1 >= lowerLimit1 && angle1 <= upperLimit1) && (angle2 >= lowerLimit2 && angle2 <= upperLimit2);
//     }

//     void FindNewTarget()
//     {
//         Vector2 nearestPoint = Vector2.zero;
//         float minDistance = float.MaxValue;

//         foreach (var point in attachmentPoints)
//         {
//             float distance = Vector2.Distance(GetFootPosition(), point);
//             if (distance < minDistance)
//             {
//                 minDistance = distance;
//                 nearestPoint = point;
//             }
//         }

//         if (minDistance < attachDistance)
//         {
//             ActivateFootHinge(nearestPoint);
//             currentTarget = nearestPoint;
//             attachmentPoints.Remove(nearestPoint); // Ensure the point is unique
//         }
//     }

//     void SetMotorSpeed(HingeJoint2D hinge, float speed)
//     {
//         JointMotor2D motor = hinge.motor;
//         motor.motorSpeed = Mathf.Clamp(speed, -maxMotorTorque, maxMotorTorque);
//         motor.maxMotorTorque = maxMotorTorque;
//         hinge.motor = motor;
//         hinge.useMotor = true;
//     }

//     void TryDetach()
//     {
//         if (isAttached)
//         {
//             float angle1 = hingeSegment1.jointAngle;
//             float angle2 = hingeSegment2.jointAngle;
//             float combinedAngle = Mathf.Abs(angle1) + Mathf.Abs(angle2);

//             if (combinedAngle >= 150f)
//             {
//                 Detach();
//             }
//         }
//     }

//     void ActivateFootHinge(Vector2 point)
//     {
//         footHinge.connectedBody = null;
//         footHinge.enabled = true;
//         footHinge.useMotor = false;
//         isAttached = true;
//     }

//     public void ReleaseFootHinge()
//     {
//         if (isAttached)
//         {
//             footHinge.connectedBody = null;
//             footHinge.enabled = false;
//             isAttached = false;
//         }
//     }

//     void Detach()
//     {
//         footHinge.connectedBody = null;
//         footHinge.enabled = false;
//         isAttached = false;
//     }

//     public Vector2 GetFootPosition()
//     {
//         float legLength = legSegment2.localScale.x + 2.3f;
//         Vector2 footPosition = (Vector2)legSegment2.position + (Vector2)legSegment2.right * legLength;
//         return footPosition;
//     }

//     public void SetInputDirection(Vector2 direction)
//     {
//         inputDirection = direction;
//     }

//     public void SetAttachmentPoints(List<Vector2> points)
//     {
//         attachmentPoints = new List<Vector2>(points); // Ensure a new list to prevent unintended modification
//     }
// }
