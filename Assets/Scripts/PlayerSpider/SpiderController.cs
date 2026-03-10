using UnityEngine;
using System.Collections.Generic;

public class SpiderController : MonoBehaviour
{
    public bool isAI = false;
    public bool checkExt;
    public Transform head;
    public SpiderLeg[] legs;
    public float motorSpeed = 100f;
    public float maxMotorTorque = 1000f;
    public float attachDistance = 5f; // Adjusted attach distance
    public float attachMod = 1.2f;
    public float sightDistance = 30f;
    public LayerMask attachableLayers;
    public Vector2 inputDirection;
    public List<AttachmentPoint> leftAttachmentPoints = new List<AttachmentPoint>();
    public List<AttachmentPoint> rightAttachmentPoints = new List<AttachmentPoint>();
    public List<AttachmentPoint> attachmentPoints = new List<AttachmentPoint>();

    public int rayCount = 300;
    public bool locked = false;
    private float legSwitchTimer = 0;
    public float legSwitchDelay = 1f;
    public int legIndex = 0;
    public float raiseTime = .3f;
    public float movementSpeed;
    public Vector2 inputOverride;
    public float lockTime = .4f;
    public float lockTimeout = 1f;
    public float motorSpeedMultiplier = 1f;
    public float reassignStepTime = 1.5f;
    public float legSightDistance = 10f;
    public float footSpeed = 200f;
    public bool useHingeThreshold = false;
    public float forceThreshold = 1f;
    public int detached = 0;
    public int maxDetached = 2;
    public float raiseSpeed = 1000f;
    public float headSafeArea = 5f;
    public float maxLegExtension = 14f;
    public float footMagnetForce = 200f;
    public float rotationSpeed = 20f;

    private int detachedL = 0;
    private int detachedR = 0;
    public bool gaitPattern;
    public float leg2RotationSpeedLockOnMod;

    public bool freezeLeg2Rotation = false;
    public float freezeLeg2Time = .1f;
    public float footPullTolerance = .5f;
    public bool footCheck = false;
    private bool closeFar = false;
    public bool urgencyGait = true;
    public float urgencyThreshold = 1.0f;
    private bool leftRight;
    private Rigidbody2D headRigidbody;
    public float headRotationSpeed = 5f;
    public Rigidbody2D headRb;
    public float segmentLength;
        private float stepTimer = 0f;
    private int currentLegGroup = 0;
    private bool lcf = false;
    private bool rcf = false;
    private int cfCounter = 0;
    public bool useCentiBodyGizmo = false;
    public float centiRaiseMax;
    public float centiRaiseMin;
    public float centiLegSliderRate;
    public float centiRestingHeight;
            public PolygonCollider2D legRef;
            public int aIFollowDistance;
            private float footSpacing = 2f;
    void Start()
    {
        // foreach (SpiderLeg leg in legs)
        // {
        //     leg.InitializeLeg(motorSpeed, maxMotorTorque, attachDistance, attachableLayers);
        // }
        if(legRef != null){
            maxLegExtension = legRef.bounds.size.x*2;
        }
        if(gameObject.tag == "Centipede"){
            return;
        }
        else{
        PerformRaycasts();
        foreach (SpiderLeg leg in legs)
        {
            leg.SetInputDirection(inputDirection);
            if (leg.isLeftSide)
            {
                leg.SetAttachmentPoints(leftAttachmentPoints);
            }
            else
            {
                leg.SetAttachmentPoints(rightAttachmentPoints);
            }
        }
        }
    }

    void Awake()
    {
        if(gameObject.tag != "Centipede")
        headRigidbody = head.GetComponent<Rigidbody2D>();
        else
        headRigidbody = headRb;
    }

    void FixedUpdate()
    {
        // MoveBody();
        PerformRaycasts();
        GetDetachedCount();
            AlignHeadToSurface();

            if(gameObject.tag == "Centipede"){
                SetNewTargets();
            }
        // Vector2 averageTargetPosition = CalculateAverageTargetPosition(legs);
        // if (averageTargetPosition != Vector2.zero)
        // {
        //     float desiredRotation = CalculateDesiredRotation(head.position, averageTargetPosition);
        //     SmoothlyRotateHead(headRigidbody, desiredRotation, rotationSpeed);
        // }
    }
    
    void Update()
    {
        if (Input.GetKeyDown(KeyCode.F))
        {
            locked = !locked;
        }
        if(!isAI)
        GetInput();

        foreach (SpiderLeg leg in legs)
        {
            leg.SetInputDirection(inputDirection);
            if (leg.isLeftSide)
            {
                leg.SetAttachmentPoints(leftAttachmentPoints);
            }
            else
            {
                leg.SetAttachmentPoints(rightAttachmentPoints);
            }
            leg.PerformRaycasts();
        }

        if (Input.GetKey(KeyCode.Space))
        {
            ReleaseAllFootHinges();
        }

        legSwitchTimer -= Time.deltaTime;
        if (inputDirection != Vector2.zero)
        {
            if (legSwitchTimer <= 0f && detached < maxDetached)
            {
                if(gameObject.tag == "Centipede"){
                    CentipedeGaitWalk();
                }
                else if (urgencyGait)
                    UrgencyGait();
                else if (gaitPattern)
                    GaitWalk();
                else
                    AlternateCloseFarWalk();
                legSwitchTimer = legSwitchDelay;
            }
        }
    }
    public void SetInputDirection(Vector2 AIDirection){

        inputDirection = AIDirection;
    }
    
void AlignHeadToSurface()
{
    // Define the directions for the raycasts (upwards, downwards, and at 45-degree angles)
    Vector2 rayDirectionUp = head.up; // Upwards from the head's perspective
    Vector2 rayDirectionDown = -head.up; // Downwards from the head's perspective
    Vector2 rayDirectionUp45 = Quaternion.Euler(0, 0, 45f) * head.up; // 45 degrees upward
    Vector2 rayDirectionDown45 = Quaternion.Euler(0, 0, -45f) * head.up; // 45 degrees downward

    // Perform the raycasts
    RaycastHit2D hitDown45 = Physics2D.Raycast(head.position, rayDirectionDown45, sightDistance, attachableLayers);
    RaycastHit2D hitUp45 = Physics2D.Raycast(head.position, rayDirectionUp45, sightDistance, attachableLayers);
    RaycastHit2D hitDown = Physics2D.Raycast(head.position, rayDirectionDown, sightDistance, attachableLayers);
    RaycastHit2D hitUp = Physics2D.Raycast(head.position, rayDirectionUp, sightDistance, attachableLayers);

    if (hitDown.collider != null)
    {
        AlignWithSurface(hitDown.normal, hitDown.collider.gameObject.layer);
    }
    else if (hitDown45.collider != null)
    {
        AlignWithSurface(hitDown45.normal, hitDown45.collider.gameObject.layer);
    }
    else if (hitUp45.collider != null)
    {
        AlignWithSurface(hitUp45.normal, hitUp45.collider.gameObject.layer);
    }
    else if (hitUp.collider != null)
    {
        AlignWithSurface(hitUp.normal, hitUp.collider.gameObject.layer);
    }
    else
    {
        headRigidbody.freezeRotation = false; // If no surface is detected, allow natural rotation
    }
}

// Helper function to align the head with the given surface normal
void AlignWithSurface(Vector2 surfaceNormal, int colliderLayer)
{
    // Check if the collider's layer is "Web" by comparing its layer index
    // if (LayerMask.LayerToName(colliderLayer) == "Web")
    // {
    //     headRigidbody.freezeRotation = true;
    //     return;
    // }

    // Otherwise, align the head with the surface normal
    headRigidbody.freezeRotation = true;

    // Calculate the angle to rotate the head to match the surface normal
    float targetAngle = Mathf.Atan2(surfaceNormal.y, surfaceNormal.x) * Mathf.Rad2Deg;

    // Rotate the head to align with the surface normal
    Quaternion targetRotation = Quaternion.Euler(0, 0, targetAngle - 90f); // Subtract 90 degrees to align the head's "up" with the surface
    head.rotation = Quaternion.Slerp(head.rotation, targetRotation, Time.deltaTime * headRotationSpeed);
}


void GaitWalk()
{
    SpiderLeg currentLeg = legs[legIndex];
    bool canDetach = false;

    // Check if the leg is on the left side and meets the detachment criteria
    if (currentLeg.isLeftSide && GetDetachedLCount() < maxDetached)
    {
        // Check if the leg has an attachment point in the direction of input
        if (HasAttachmentPointInInputDirection(currentLeg))
        {
            canDetach = true;
        }
    }
    // Check if the leg is on the right side and meets the detachment criteria
    else if (!currentLeg.isLeftSide && GetDetachedRCount() < maxDetached)
    {
        // Check if the leg has an attachment point in the direction of input
        if (HasAttachmentPointInInputDirection(currentLeg))
        {
            canDetach = true;
        }
    }

    // Detach the leg if it has an attachment point in range
    if (canDetach)
    {
        currentLeg.Detach();
    }

    legIndex++;

    if (legIndex >= legs.Length)
    {
        legIndex = 0;
    }
}
void CentipedeGaitWalk()
{
    SpiderLeg currentLeg = legs[legIndex];

    if (GetDetachedCount() < (float)maxDetached)
    {
        currentLeg.Detach();
    }

    legIndex++;

    if (legIndex >= legs.Length)
    {
        legIndex = 0;
    }
}

void UrgencyGait()
{
    if (GetDetachedCount() >= (float)maxDetached) return;

    SpiderLeg bestLeg = null;
    float maxUrgency = urgencyThreshold;

    foreach (SpiderLeg leg in legs)
    {
        if (!leg.isAttached) continue;

        // Side balance: Don't detach too many on one side. 
        if (leg.isLeftSide && GetDetachedLCount() >= maxDetached / 2) continue;
        if (!leg.isLeftSide && GetDetachedRCount() >= maxDetached / 2) continue;

        float urgency = leg.GetStepUrgency(inputDirection);
        if (urgency > maxUrgency)
        {
            maxUrgency = urgency;
            bestLeg = leg;
        }
    }

    if (bestLeg != null)
    {
        bestLeg.Detach();
    }
}

public bool IsPointTooCloseToOtherFeet(Vector2 point, SpiderLeg excludeLeg)
{
    foreach (SpiderLeg leg in legs)
    {
        if (leg == excludeLeg || !leg.isAttached) continue;
        // Avoid placing feet too close to each other to prevent "tripping"
        if (Vector2.Distance(point, leg.GetFootPosition()) < footSpacing)
            return true;
    }
    return false;
}
// Helper function to check if the leg has an attachment point in the direction of input
bool HasAttachmentPointInInputDirection(SpiderLeg leg)
{
    Vector2 inputDirectionn = inputDirection.normalized;

    foreach (AttachmentPoint point in leg.attachmentPoints)
    {
        Vector2 directionToPoint = (point.point - (Vector2)leg.GetFootPosition()).normalized;

        // Calculate the angle between the input direction and the direction to the attachment point
        float angle = Vector2.Angle(inputDirectionn, directionToPoint);

        // If the angle is small enough, the point is considered to be in the direction of input
        if (angle < 45f) // Adjust this threshold as necessary
        {
            return true; // Attachment point found in the direction of input
        }
    }

    return false; // No attachment point found in the direction of input
}



    void AlternateCloseFarWalk()
    {
        if (leftRight)
        {
            LStep();
        }
        else
        {
            RStep();
        }

        leftRight = !leftRight;
    }

    void LStep()
    {
        SpiderLeg foot = null;
        if (lcf)
        {
            foot = GetClosestFoot(true, true);
        }
        else
        {
            foot = GetFurthestFoot(true, true);
        }
        if (foot != null)
        {
            foot.Detach();
            lcf = !lcf;
        }
    }

    void RStep()
    {
        SpiderLeg foot = null;
        if (rcf)
        {
            foot = GetClosestFoot(true, false);
        }
        else
        {
            foot = GetFurthestFoot(true, false);
        }
        if (foot != null)
        {
            foot.Detach();
            rcf = !rcf;
        }
    }


    public SpiderLeg GetClosestFoot(bool alternate = false, bool isLeft = false)
    {
        SpiderLeg closestLeg = null;
        float minDistance = float.MaxValue;

        foreach (SpiderLeg leg in legs)
        {
            float distance = Vector2.Distance(head.position, leg.GetFootPosition());
            if (alternate)
            {
                if (isLeft && GetDetachedLCount() < maxDetached / 2)
                {
                    if (leg.isLeftSide && leg.isAttached)
                    {
                        minDistance = distance;
                        closestLeg = leg;
                    }
                }
                else if (!isLeft && GetDetachedRCount() < maxDetached / 2)
                {
                    if (!leg.isLeftSide && leg.isAttached)
                    {
                        minDistance = distance;
                        closestLeg = leg;
                    }
                }
            }
            else if (distance < minDistance && leg.isAttached)
            {
                minDistance = distance;
                closestLeg = leg;
            }
        }

        return closestLeg;
    }

    public SpiderLeg GetFurthestFoot(bool alternate = false, bool isLeft = false)
    {
        SpiderLeg furthestLeg = null;
        float maxDistance = float.MinValue;

        foreach (SpiderLeg leg in legs)
        {
            float distance = Vector2.Distance(head.position, leg.GetFootPosition());
            if (alternate)
            {
                if (isLeft && GetDetachedLCount() < maxDetached / 2)
                {
                    if (leg.isLeftSide && leg.isAttached)
                    {
                        if (distance > maxDistance && leg.isAttached)
                        {
                            maxDistance = distance;
                            furthestLeg = leg;
                        }
                    }
                }
                else if (!isLeft && GetDetachedRCount() < maxDetached / 2)
                {
                    if (!leg.isLeftSide && leg.isAttached)
                    {
                        if (distance > maxDistance && leg.isAttached)
                        {
                            maxDistance = distance;
                            furthestLeg = leg;
                        }
                    }
                }
            }
            else if (distance > maxDistance && leg.isAttached)
            {
                maxDistance = distance;
                furthestLeg = leg;
            }
        }

        cfCounter++;
        return furthestLeg;
    }

    void RotateHead()
    {
        if (inputDirection != Vector2.zero)
        {
            float angle = Mathf.Atan2(inputDirection.y, inputDirection.x) * Mathf.Rad2Deg;
            Quaternion targetRotation = Quaternion.Euler(0, 0, angle);
            head.rotation = Quaternion.Slerp(head.rotation, targetRotation, Time.deltaTime * 5f);
        }
    }

    float GetDetachedCount()
    {
        detached = 0;
        foreach (SpiderLeg leg in legs)
        {
            if (!leg.isAttached)
            {
                detached++;
            }
        }
        return detached;
    }

    float GetDetachedLCount()
    {
        detached = 0;
        foreach (SpiderLeg leg in legs)
        {
            if (!leg.isAttached && leg.isLeftSide)
            {
                detachedL++;
            }
        }
        return detached;
    }

    float GetDetachedRCount()
    {
        detached = 0;
        foreach (SpiderLeg leg in legs)
        {
            if (!leg.isAttached && !leg.isLeftSide)
            {
                detachedR++;
            }
        }
        return detached;
    }

    void GetInput()
    {
        if (locked)
        {
            inputDirection = inputDirection;
        }
        else
        {
            inputDirection = new Vector2(
                Input.GetAxis("Horizontal"),
                Input.GetAxis("Vertical")
            ).normalized;
        }

        if (Input.GetKey(KeyCode.Keypad4))
        {
            head.rotation = new Quaternion(0, 0, head.rotation.z + .001f, 1);
        }
        else if (Input.GetKey(KeyCode.Keypad5))
        {
            head.rotation = new Quaternion(0, 0, head.rotation.z - .001f, 1);
        }
    }

    // void PerformRaycasts()
    // {
    //     leftAttachmentPoints.Clear();
    //     rightAttachmentPoints.Clear();
    //     float angleStep = 360f / rayCount;

    //     for (int i = 0; i < rayCount; i++)
    //     {
    //         float angle = i * angleStep;
    //         Vector2 direction = new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad));
    //         RaycastHit2D hit = Physics2D.Raycast(head.position, direction, sightDistance, attachableLayers);

    //         if (hit.collider != null)
    //         {
    //             Vector2 hitPoint = hit.point;
    //             Vector2 hitNormal = hit.normal; // Store the normal of the hit surface

    //             if (hitPoint.x < head.position.x)
    //             {
    //                 leftAttachmentPoints.Add(new AttachmentPoint(hitPoint, hitNormal));
    //             }
    //             else
    //             {
    //                 rightAttachmentPoints.Add(new AttachmentPoint(hitPoint, hitNormal));
    //             }
    //         }
    //     }
    // }
void PerformRaycasts()
{
    leftAttachmentPoints.Clear();
    rightAttachmentPoints.Clear();
    float angleStep = 360f / rayCount;

    for (int i = 0; i < rayCount; i++)
    {
        float angle = i * angleStep;
        Vector2 direction = new Vector2(
            Mathf.Cos(angle * Mathf.Deg2Rad),
            Mathf.Sin(angle * Mathf.Deg2Rad)
        );

        // First, perform a raycast ignoring the "Web" layer to detect real surfaces.
        RaycastHit2D hit = Physics2D.Raycast(
            head.position,
            direction,
            sightDistance,
            LayerMask.GetMask("Default")
        );

        if (hit.collider != null)
        {
            Vector2 hitPoint = hit.point;
            Vector2 hitNormal = hit.normal;
            // Convert hit point to local space relative to the head
            Vector2 localHitPoint = head.InverseTransformPoint(hitPoint);
            if(!isAI)
        GlobalAttachmentManager.StoreAttachmentPoint(hit.point, hit.normal);
            if (localHitPoint.x < 0)
            {
                leftAttachmentPoints.Add(new AttachmentPoint(hitPoint, hitNormal));
            }
            else
            {
                rightAttachmentPoints.Add(new AttachmentPoint(hitPoint, hitNormal));
            }
        }
        
        // Now, perform another raycast that includes the "Web" layer, to detect webs in the path.
        RaycastHit2D[] webHits = Physics2D.RaycastAll(
            head.position,
            direction,
            sightDistance,
            LayerMask.GetMask("Web")
        );
        foreach(RaycastHit2D webHit in webHits){
        if (webHit.collider != null)
        {
            Vector2 hitPoint = webHit.point;
            Vector2 hitNormal = webHit.normal;
            // Convert hit point to local space relative to the head
            Vector2 localHitPoint = head.InverseTransformPoint(hitPoint);
            if(!isAI)
        GlobalAttachmentManager.StoreAttachmentPoint(webHit.point, webHit.normal);
            if (localHitPoint.x < 0)
            {
                leftAttachmentPoints.Add(new AttachmentPoint(hitPoint, hitNormal));
            }
            else
            {
                rightAttachmentPoints.Add(new AttachmentPoint(hitPoint, hitNormal));
            }
        }
        }
    }
}


    public List<AttachmentPoint> GetAttachmentPoints()
    {
        List<AttachmentPoint> allPoints = new List<AttachmentPoint>();
        allPoints.AddRange(leftAttachmentPoints);
        allPoints.AddRange(rightAttachmentPoints);
        return allPoints;
    }

    void SetNewTargets()
    {
        foreach (SpiderLeg leg in legs)
        {
            leg.SetNewTarget();
        }
    }

    void ReleaseAllFootHinges()
    {
        foreach (SpiderLeg leg in legs)
        {
            leg.ReleaseFootHinge();
        }
    }

    Vector2 CalculateAverageTargetPosition(SpiderLeg[] legs)
    {
        Vector2 averageTarget = Vector2.zero;
        int count = 0;

        foreach (SpiderLeg leg in legs)
        {
            if (leg.currentTarget != Vector2.zero)
            {
                averageTarget += leg.currentTarget;
                count++;
            }
        }

        if (count > 0)
        {
            averageTarget /= count;
        }

        return averageTarget;
    }

    float CalculateDesiredRotation(Vector2 headPosition, Vector2 averageTarget)
    {
        Vector2 direction = averageTarget - headPosition;
        float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
        return angle;
    }

    void SmoothlyRotateHead(Rigidbody2D headRigidbody, float desiredRotation, float rotationSpeed)
    {
        float currentRotation = headRigidbody.rotation;
        float rotationStep = rotationSpeed * Time.deltaTime;
        float newRotation = Mathf.MoveTowardsAngle(currentRotation, desiredRotation, rotationStep);

        // Apply the new rotation
        headRigidbody.rotation = newRotation;

        // Debug logs to help understand the rotation process
        Debug.Log(
            $"Current Rotation: {currentRotation}, Desired Rotation: {desiredRotation}, New Rotation: {newRotation}"
        );
    }
    public GameObject[] GetLegs()
    {
        List<GameObject> legGOs = new List<GameObject>(); // Use List for dynamic addition

        foreach (SpiderLeg leg in legs)
        {
            legGOs.Add(leg.gameObject);               // Add the leg's main GameObject
            legGOs.Add(leg.legSegment2.gameObject);   // Add the leg segment's GameObject
        }

        return legGOs.ToArray(); // Convert List to array before returning
    }
}

public struct AttachmentPoint
{
    public Vector2 point;
    public Vector2 normal;

    public AttachmentPoint(Vector2 point, Vector2 normal)
    {
        this.point = point;
        this.normal = normal;
    }
}
