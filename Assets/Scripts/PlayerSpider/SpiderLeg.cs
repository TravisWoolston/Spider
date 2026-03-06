using System.Collections.Generic;
using UnityEngine;

public class SpiderLeg : MonoBehaviour
{
    public Transform legSegment1;

    public Transform legSegment2;

    public Transform head;

    public int attachCount;

    public float distanceToTarget;

    public bool isLeftSide; // Indicates if the leg is on the left side of the spider

    public bool isAttached = false; // Made public for Gizmo access

    private bool hinge2HitLimit = false;

    public bool isWalking;

    public bool nextStepAssigned;

    public bool isCollidingWithDefault;

    public bool lockThigh;

    public Vector2 currentTarget; // Made public for Gizmo access
    public Vector2 currentNormal;
    public Vector2 lastTarget;
    public float targetPerpAngle;
    public Vector2 inputDirection; // Made public for Gizmo access

    public float motorSpeed;

    public float maxMotorTorque;

    public LayerMask attachableLayers;

    public HingeJoint2D hingeSegment1;

    public HingeJoint2D hingeSegment2;

    public HingeJoint2D footHinge;

    public List<AttachmentPoint> attachmentPoints;

    public Dictionary<HingeJoint2D, float> previousAngles = new Dictionary<HingeJoint2D, float>();

    public Dictionary<HingeJoint2D, float> previousDirection =
        new Dictionary<HingeJoint2D, float>();

    public float motorUpdateTime = .5f; // Time interval to update motor speeds

    public float motorUpdateTimer;
    public float lockTimer = 0f;

    public float lockTimeoutTimer = 0f;

    public float reassignStepTimer = 0f;

    private float raiseTime = .2f;

    public float raiseTimer = 10f;

    public float lastMotorSpeed;

    public float lastMotorSpeedAgain;

    public float lastMotorSpeed2;

    public float previousDistanceToTarget2;

    public float gizTargetAngle;

    float combinedLength;

    public float angle1;

    public float angle2;

    public List<AttachmentPoint> footAttachmentPoints = new List<AttachmentPoint>();

    private int rayCount = 30;


    public List<AttachmentPoint> points;

    public SpiderController spiderController;

    public float currentLength;

    public float maxLength;

    public float minLength;

    public float maxExtensionFactor = 0.9f;

    private float minContractionFactor = 0.5f;

    public float lastAngle;

    private Rigidbody2D segment1;

    private Rigidbody2D segment2;

    public float segmentLength = 6.82f;

    public float segmentWidth = 0.37f; // Width of the segments

    private Vector2 targetPosition;

    private Vector3 believedSegment1Position;

    private Vector3 believedSegment2Position;


    public Vector3 basePosition;

    public float targetAngle1;

    public float targetAngle2;

    public float ogAngle1;

    public float ogAngle2;

    public float animationDuration = 2f;

    public float maxLimit1;

    public float minLimit1;

    public float maxLimit2;

    public float minLimit2;

    public Vector2 previousInputDirection;

    public float jointForce;

    private JointMotor2D footMotor;
    private float aimAngle = 0;
    private bool aimSet = false;
    private Collider2D segment2Collider;
    private bool isCollisionActive = true;
    public bool useMotor1 = false;
    private float freezeLeg2Timer = 0f;
    public float searchRadius = 5f;
    public float rawMaxExtension;

    void Awake()
    {
        if(gameObject.tag == "Centipede"){
        if(legSegment1 == null){
                                legSegment1 =  transform.GetChild(0).transform;
        legSegment2 = transform.GetChild(1).transform;
        }
        head = gameObject.GetComponent<Rigidbody2D>().transform;
        }

        hingeSegment1 = legSegment1.GetComponent<HingeJoint2D>();
        HingeJoint2D[] hinges = legSegment2.GetComponents<HingeJoint2D>();
        hingeSegment2 = hinges[0];
        footHinge = hinges[1];
        segment1 = legSegment1.gameObject.GetComponent<Rigidbody2D>();
        segment2 = legSegment2.gameObject.GetComponent<Rigidbody2D>();
        segment2Collider = legSegment2.GetComponent<Collider2D>();
        attachmentPoints = new List<AttachmentPoint>();
    }

    void Start()
    {
        this.motorSpeed = spiderController.motorSpeed;
        this.spiderController.maxMotorTorque = spiderController.maxMotorTorque;

        this.attachableLayers = spiderController.attachableLayers;
        footMotor = footHinge.motor;

        footHinge.enabled = false;

        // Initialize the rotation history
        previousAngles[hingeSegment1] = hingeSegment1.jointAngle;
        previousAngles[hingeSegment2] = hingeSegment2.jointAngle;
        previousDirection[hingeSegment1] = 0;
        previousDirection[hingeSegment2] = 0;
        combinedLength = GetCombinedLegLength();

        // Initialize positions
        believedSegment1Position = legSegment1.position;
        believedSegment2Position = legSegment2.position;
        SetNewTarget();
        minLimit1 =
            hingeSegment1.limits.min < hingeSegment1.limits.max
                ? hingeSegment1.limits.min
                : hingeSegment1.limits.max;
        maxLimit1 =
            hingeSegment1.limits.max > hingeSegment1.limits.min
                ? hingeSegment1.limits.max
                : hingeSegment1.limits.min;
        minLimit2 =
            hingeSegment2.limits.min < hingeSegment2.limits.max
                ? hingeSegment2.limits.min
                : hingeSegment2.limits.max;
        maxLimit2 =
            hingeSegment2.limits.max > hingeSegment2.limits.min
                ? hingeSegment2.limits.max
                : hingeSegment2.limits.min;

                 segmentLength = spiderController.segmentLength;
                 if(spiderController.legRef != null){
                    segmentLength = spiderController.legRef.bounds.size.x/2;
                 }
                rawMaxExtension = segmentLength*2;
    }

    void FixedUpdate()
    {
        attachCount = attachmentPoints.Count;
        motorUpdateTimer += Time.fixedDeltaTime;
        raiseTimer += Time.fixedDeltaTime;
        lockTimer += Time.fixedDeltaTime;
        lockTimeoutTimer += Time.fixedDeltaTime;
        jointForce = footHinge.reactionTorque;
        targetPosition =
            (Vector2)head.position
            + inputDirection * spiderController.movementSpeed * Time.deltaTime;

        if (inputDirection == Vector2.zero)
        {
            isWalking = false;
        }
        else if (isAttached) { }
        if (reassignStepTimer > spiderController.reassignStepTime)
        {
            nextStepAssigned = false;

            // isWalking = true;
            reassignStepTimer = 0f;
            SetNewTarget();
        }

        if (!isAttached)
        {
            if(gameObject.tag != "Centipede")
            hingeSegment1.useMotor = useMotor1;
            PullFootToTarget();
            // MoveToAttachmentPoint();
            // hingeSegment2.useMotor = false;

            hingeSegment2.useLimits = false;
            reassignStepTimer += Time.deltaTime;
                        if((spiderController.freezeLeg2Time < freezeLeg2Timer && segment2.freezeRotation) || !spiderController.freezeLeg2Rotation){
                segment2.freezeRotation = false;
                freezeLeg2Timer = 0f;
            } else {
                freezeLeg2Timer += Time.fixedDeltaTime;
            }
        }
        else
        {
            // CheckForceOnFootJoint();
        
            segment2.freezeRotation = false;
            if (spiderController.footCheck)
                FootDetachCheck();
            // hingeSegment2.useLimits = true;
            reassignStepTimer = 0f;
        }
            // Check if the joint is being stretched by a large force
            if(spiderController.useHingeThreshold)
    if (hingeSegment2.reactionForce.magnitude > spiderController.forceThreshold)
    {
        Detach();
    }
    }

    void Update()
    {
        if (isAttached)
        {
            aimSet = false;
            if (inputDirection == Vector2.zero)
            {
                LockLeg();
            }
            else
            {
                MoveBody();
                // if (spiderController.detached < spiderController.maxDetached)
                if (spiderController.checkExt)
                    CheckExtensionAndDetach();
            }
        }
        else
        {
            // SetNewTarget();
            // MoveToAttachmentPoint();
            TryAttach();
            // Leg1StepPrep();
            // PullFootToTarget();
        }
        // hingeSegment1.useMotor =false;
        // if(applyingForce){
        //     hingeSegment1.useLimits = false;
        // }else{
        //     hingeSegment1.useLimits = true;
        // }
        // if (
        //     !isAttached
        //     && Vector2.Distance(head.position, currentTarget) > GetCombinedLegLength() + 5
        // )
        // {
        //     SetMotorSpeed(hingeSegment1, 0);
        //     SetMotorSpeed(hingeSegment2, 0);
        // }
        if(gameObject.tag == "Centipede"){
           hingeSegment1.useLimits = true;
        }
    }

    void FootCollisionPhaser()
    {
        if (raiseTimer < raiseTime)
        {
            // If collision is currently active, deactivate it
            if (isCollisionActive)
            {
                segment2Collider.enabled = false;
                isCollisionActive = false;
                Debug.Log("Collision deactivated for legSegment2");
            }
        }
        else
        {
            // If collision is currently inactive, reactivate it
            if (!isCollisionActive)
            {
                segment2Collider.enabled = true;
                isCollisionActive = true;
                Debug.Log("Collision reactivated for legSegment2");
            }
        }
    }

    public void PerformRaycasts()
    {
        float angleStep = 360f / rayCount;
        if (points == null)
            return;
        points.Clear();

        for (int i = 0; i < rayCount; i++)
        {
            float angle = i * angleStep;
            Vector2 direction = new Vector2(
                Mathf.Cos(angle * Mathf.Deg2Rad),
                Mathf.Sin(angle * Mathf.Deg2Rad)
            );
            RaycastHit2D hit = Physics2D.Raycast(
                GetFootPosition(),
                direction,
                spiderController.legSightDistance,
                attachableLayers
            );
            if (hit.collider != null)
            {
                Vector2 hitPoint = hit.point;
                Vector2 hitNormal = hit.normal;
                AttachmentPoint AP = new AttachmentPoint(hitPoint, hitNormal);
                if (points.Contains(AP))
                {
                    continue;
                }
                if(gameObject.tag != "Centipede")
                if (Vector2.Distance(head.position, hitPoint) > GetCombinedLegLength())
                {
                    continue;
                }
                // if (Vector2.Distance(hitPoint, head.position) > spiderController.legSightDistance)
                //     continue;
                // if (!isLeftSide && hitPoint.x < head.position.x)
                // {
                //     continue;
                // }
                // if (isLeftSide && hitPoint.x > head.position.x)
                // {
                //     continue;
                // }
                points.Add(AP);
            }
        }
        // for (int i = 0; i < points.Count; i++)
        // {
        //     if (Vector2.Distance(points[i], head.position) > spiderController.legSightDistance)
        //     {
        //         points.Remove(points[i]);
        //         i--;
        //     }
        // }
        footAttachmentPoints = points;
        if (isLeftSide)
            spiderController.leftAttachmentPoints.AddRange(points);
        else
            spiderController.rightAttachmentPoints.AddRange(points);
    }

    void MoveToAttachmentPoint()
    {
        if (currentTarget != Vector2.zero)
        {
            Vector2 targetDirection = currentTarget - (Vector2)legSegment2.position;

            float currentAngle1 = hingeSegment1.jointAngle;
            float currentAngle2 = hingeSegment2.jointAngle;

            float targetAngle1 = CalculateTargetAngle(legSegment1.right, targetDirection);
            angle1 = targetAngle1;
            float targetAngle2 = CalculateTargetAngle(legSegment2.right, targetDirection);
            angle2 = targetAngle2;
            CalculateMotorSpeed(currentAngle1, targetAngle1, hingeSegment1);
            // float motorSpeed2 = CalculateMotorSpeedLearning(
            //     currentAngle2,
            //     targetAngle2,
            //     hingeSegment2
            // );


            // Leg1StepPrep();
            // if(hingeSegment2.useMotor)
            // SetMotorSpeed(hingeSegment2, motorSpeed2);
        }
    }

    void MoveBody()
    {
        // if (motorUpdateTimer >= motorUpdateTime)
        // {
        Vector2 targetDirection =
            inputDirection != Vector2.zero
                ? inputDirection
                : currentTarget - (Vector2)legSegment2.position;
        targetPosition =
            (Vector2)head.position
            + inputDirection * spiderController.movementSpeed * Time.deltaTime;

        // float currentAngle1 = hingeSegment1.jointAngle;
        // float currentAngle2 = hingeSegment2.jointAngle;
        // CalculateIK(targetPosition);
        CalculateFootHingeMotorSpeed();
        // SetMotorSpeed(hingeSegment1, motorSpeed1);
        // SetMotorSpeed(hingeSegment2, motorSpeed2);
        motorUpdateTimer = 0f; // Reset the motor update timer
        // }
    }

    void TryAttach()
    {
        if (!isAttached && attachmentPoints != null)
        {
            Vector2 nearestPoint = Vector2.zero;
            float minDistance = float.MaxValue;
            Vector2 footPosition = GetFootPosition();
            // Check if the foot is colliding with the default layer
            isCollidingWithDefault = false;
            Collider2D[] colliders = Physics2D.OverlapCircleAll(footPosition, 0.2f); // Adjust the radius as needed
            Collider2D[] targetColliders = Physics2D.OverlapCircleAll(footPosition, .2f);
            bool targetIsWeb = false;
            bool collidingWithWeb = false;
            foreach (var collider in targetColliders)
            {
                if (collider.gameObject.layer == LayerMask.NameToLayer("Web"))
                {
                    targetIsWeb = true;
                }
            }
            foreach (var collider in colliders)
            {
                if (collider.gameObject.layer == LayerMask.NameToLayer("Web"))
                {
                    collidingWithWeb = true;
                }
                if (
                    collider.gameObject.layer == LayerMask.NameToLayer("Default")
                    || collider.gameObject.layer == LayerMask.NameToLayer("Web")
                )
                {
                    isCollidingWithDefault = true;

                    break;
                }
            }
            if (
                targetIsWeb
                && collidingWithWeb
                && Vector2.Distance(footPosition, currentTarget)
                    < spiderController.legSightDistance / 2
                && raiseTimer > spiderController.raiseTime
            )
            {
                ActivateFootHinge(currentTarget, currentNormal);
                // attachmentPoints.Remove(nearestPoint); // Ensure the point is unique
            }
            // Temporarily extend the attach distance if colliding with the default layer
            float effectiveAttachDistance =
                isCollidingWithDefault && !isWalking
                    ? spiderController.attachDistance * spiderController.attachMod
                    : spiderController.attachDistance; // Adjust the multiplier as needed

            Vector2 footDirection = (footPosition - (Vector2)head.position).normalized;

            // Check if the input direction is in the same direction as the foot
            bool isInputDirectionTowardsFoot = Vector2.Dot(inputDirection, footDirection) > -.5f;

            if (
                Vector2.Distance(GetFootPosition(), currentTarget) < effectiveAttachDistance
                && raiseTimer > spiderController.raiseTime
            )
            {
                ActivateFootHinge(currentTarget, currentNormal);
                // attachmentPoints.Remove(nearestPoint); // Ensure the point is unique
            }
            // if (isCollidingWithDefault)
            // {
            //     raiseTimer = 0;
            // }
        }
    }

    void CheckExtensionAndDetach()
    {
        currentLength = Vector2.Distance(head.position, GetFootPosition());
        maxLength = GetCombinedLegLength() * maxExtensionFactor;
        minLength = GetCombinedLegLength() * minContractionFactor;

        Vector2 footPosition = GetFootPosition();
        Vector2 footDirection = (footPosition - (Vector2)head.position).normalized;

        // Check if the input direction is in the same direction as the foot
        bool isInputDirectionTowardsFoot = Vector2.Dot(inputDirection, footDirection) > 0;
        // if (isLeftSide && footPosition.x > head.position.x && inputDirection.y == 0
        // // &&spiderController.detached < spiderController.maxDetached
        // )
        // {
        //     raiseTimer = 0;
        //     Detach();
        // }
        // else if (!isLeftSide && footPosition.x < head.position.x && inputDirection.y == 0
        // // &&spiderController.detached < spiderController.maxDetached
        // )
        // {
        //     raiseTimer = 0;
        //     Detach();
        // }

        if (
            currentLength > GetCombinedLegLength()
            || Vector2.Distance(head.position, GetFootPosition()) < spiderController.headSafeArea
        // && spiderController.detached < spiderController.maxDetached
        )
        {
            Detach();
        }
    }

    float CalculateTargetAngleForBody(Vector2 legDirection, Vector2 targetDirection)
    {
        float angle = Vector2.SignedAngle(legDirection, targetDirection);
        return isLeftSide ? angle : -angle; // Adjust angle for left and right legs
    }

    float CalculateTargetAngle(Vector2 legDirection, Vector2 targetDirection)
    {
        float angle = Vector2.SignedAngle(legDirection, targetDirection);
        if (isLeftSide)
        {
            angle = -angle;
        }
        return angle;
    }

    float CalculateMotorSpeedForBody(
        float currentAngle,
        float targetAngle,
        HingeJoint2D hinge,
        int segment
    )
    {
        // Define a tolerance for stopping the motor
        float tolerance = 1f; // Adjust this value as needed

        // If the angle difference is within the tolerance, stop the motor
        // Get the hinge limits
        JointAngleLimits2D limits = hinge.limits;

        // Check if rotating in the current direction would exceed the limits
        float angleDifference = Mathf.DeltaAngle(currentAngle, targetAngle);
        float direction = Mathf.Sign(angleDifference);

        if (!isLeftSide && segment == 2)
        {
            if (inputDirection.x < 0)
                direction = -Mathf.Abs(direction);
            else if (inputDirection.x > 0)
                direction = Mathf.Abs(direction);
        }
        else if (segment == 2)
        {
            if (inputDirection.x < 0)
                direction = Mathf.Abs(direction);
            else if (inputDirection.x > 0)
                direction = -Mathf.Abs(direction);
        }
        else if (segment == 1)
        {
            Debug.Log(Mathf.Abs(angleDifference) + " " + gameObject.name);
            float newAngle = currentAngle + direction * Mathf.Abs(angleDifference);

            if (Mathf.Abs(angleDifference) < tolerance)
            {
                Debug.Log("correct angle " + gameObject.name);
                return 0f;
            }
            return 0f;
        }

        return Mathf.Clamp(
            direction * motorSpeed,
            -spiderController.maxMotorTorque,
            spiderController.maxMotorTorque
        );
    }

    void Leg1StepPrep()
    {
        Vector2 currentTargetPosition = head.InverseTransformPoint(currentTarget);
        Vector2 knee = legSegment1.transform.TransformPoint(hingeSegment2.connectedAnchor);
        Vector2 currentKneePosition = head.InverseTransformPoint(knee);
        Vector2 footPosition = head.InverseTransformPoint(GetFootPosition());
        Vector2 currentFootPosition = head.InverseTransformPoint(footPosition);
        float kneeToTargetDistance = Vector2.Distance(knee, currentTarget);
        if (
            kneeToTargetDistance > segmentLength - spiderController.attachDistance
            && kneeToTargetDistance < segmentLength + spiderController.attachDistance
        )
        {
            SetMotorSpeed(hingeSegment1, 0);
        }
        else
        {
            float raiseSpeed = isLeftSide
                ? Mathf.Abs(spiderController.raiseSpeed)
                : -Mathf.Abs(spiderController.raiseSpeed);
            // Debug.Log(currentTargetPosition.y + " " + currentKneePosition.y);
            raiseSpeed =
                currentTargetPosition.y > currentKneePosition.y
                && currentFootPosition.y > currentKneePosition.y
                    ? -raiseSpeed
                    : raiseSpeed;
            // raiseSpeed = currentTargetPosition.y < 5 ? raiseSpeed : -raiseSpeed;
            // if (currentTargetPosition.y > 5)
            //     Debug.Log("currentTargetPosition.y > knee.y");
            // Debug.Log(currentTargetPosition + " " + currentTarget);
            SetMotorSpeed(
                hingeSegment1,
                Mathf.Clamp(
                    raiseSpeed,
                    -spiderController.maxMotorTorque,
                    spiderController.maxMotorTorque
                )
            );
        }
    }

    void CalculateMotorSpeed(float currentAngle, float targetAngle, HingeJoint2D hinge)
    {
        // hingeSegment2.useMotor = true;
        Vector2 knee = legSegment1.transform.TransformPoint(hingeSegment2.connectedAnchor);
        // if (lockTimer < spiderController.lockTime && raiseTimer > raiseTime)
        // {
        //     return 0;
        // }
        float angleDifference = Mathf.DeltaAngle(currentAngle, targetAngle);
        float direction = Mathf.Sign(angleDifference);
        gizTargetAngle = targetAngle;

        distanceToTarget = Vector2.Distance(GetFootPosition(), currentTarget);
        float distanceMultiplier = 10 / (spiderController.legSightDistance * distanceToTarget);

        // Get the hinge limits
        JointAngleLimits2D limits = hinge.limits;

        if (!isLeftSide)
        {
            direction = -direction;
        }
        if (raiseTimer < spiderController.raiseTime)
        {
            float raiseSpeed = isLeftSide
                ? Mathf.Abs(spiderController.raiseSpeed)
                : -Mathf.Abs(spiderController.raiseSpeed);
            Vector2 currentTargetPosition = head.InverseTransformPoint(currentTarget);
            raiseSpeed = currentTargetPosition.y < 5 ? raiseSpeed : -raiseSpeed;
            if (currentTargetPosition.y > 5)
                Debug.Log("target above head");
            // Debug.Log(currentTargetPosition + " " + currentTarget);
            lastMotorSpeed = Mathf.Clamp(
                raiseSpeed,
                -spiderController.maxMotorTorque,
                spiderController.maxMotorTorque
            );
            if (Vector2.Distance(lastTarget, GetFootPosition()) > segmentLength / 2)
            {
                // raiseTimer += raiseTime;
            }
            else
            {
                raiseTimer = 0;
            }
            // hinge2HitLimit = false;
            SetMotorSpeed(hingeSegment1, lastMotorSpeed);
            hingeSegment1.useMotor = true;
        }
        else
        {
            hingeSegment1.useMotor = false;
        }
        // else if (
        //     (currentTarget.y < head.position.y && knee.y > head.position.y)
        //     || (
        //         currentTarget.y < head.position.y
        //         && Vector2.Distance(currentTarget, head.position) > GetCombinedLegLength() * .7
        //     )
        // )
        // {
        //     if (!isLeftSide)
        //     {
        //         lastMotorSpeed = Mathf.Abs(motorSpeed * distanceMultiplier);
        //     }
        //     else
        //     {
        //         lastMotorSpeed = -Mathf.Abs(motorSpeed * distanceMultiplier);
        //     }
        // SetMotorSpeed(hingeSegment1, lastMotorSpeed);;
        // }
        // else
        // {
        //     if (!isLeftSide)
        //     {
        //         lastMotorSpeed = -Mathf.Abs(motorSpeed * distanceMultiplier);
        //     }
        //     else
        //     {
        //         lastMotorSpeed = Mathf.Abs(motorSpeed * distanceMultiplier);
        //     }
        // SetMotorSpeed(hingeSegment1, lastMotorSpeed);;
        // }
    }

    float CalculateMotorSpeedLearning(float currentAngle, float targetAngle, HingeJoint2D hinge)
    {
        float angleDifference = Mathf.DeltaAngle(currentAngle, targetAngle);
        float direction = Mathf.Sign(angleDifference);
        if (!isLeftSide)
        {
            direction = -direction;
        }
        JointAngleLimits2D limits = hingeSegment2.limits;
        float hinge1Diff = Mathf.Abs(lastAngle) - Mathf.Abs(hingeSegment1.jointAngle);

        float newAngle = currentAngle + direction * Mathf.Abs(angleDifference);
        if (motorUpdateTimer > motorUpdateTime)
        {
            hinge2HitLimit = false;
            motorUpdateTimer = 0;
        }

        // if (
        //     hingeSegment2.jointAngle < limits.min + 1
        //     || hingeSegment2.jointAngle > limits.max - 1
        //     || (direction > 0 && newAngle > limits.max)
        //     || (direction < 0 && newAngle < limits.min)
        // )
        // {
        //     hinge2HitLimit = true;
        // }
        // Check if the last motor speed brought it closer or further from the target
        distanceToTarget = Vector2.Distance(GetFootPosition(), currentTarget) - hinge1Diff;
        float distanceMultiplier = 10 / (spiderController.legSightDistance * distanceToTarget);
        if (distanceToTarget < previousDistanceToTarget2)
        {
            // If closer, continue in the same direction
            float speed =
                spiderController.motorSpeed * (distanceMultiplier) * (lastMotorSpeed2 > 0 ? 1 : -1);
            lastMotorSpeed2 = Mathf.Clamp(
                Mathf.Abs(speed) < motorSpeed ? motorSpeed * (lastMotorSpeed2 > 0 ? 1 : -1) : speed,
                -spiderController.maxMotorTorque,
                spiderController.maxMotorTorque
            );
        }
        else if (currentTarget.y < head.position.y && GetFootPosition().y > head.position.y)
        {
            if (!isLeftSide)
            {
                lastMotorSpeed2 = Mathf.Abs(spiderController.motorSpeed * (distanceMultiplier));
            }
            else
            {
                lastMotorSpeed2 = -Mathf.Abs(spiderController.motorSpeed * (distanceMultiplier));
            }
        }
        else if (currentTarget.y > head.position.y && GetFootPosition().y < head.position.y)
        {
            if (!isLeftSide)
            {
                if (currentTarget.x > GetFootPosition().x)
                {
                    lastMotorSpeed2 = -Mathf.Abs(
                        spiderController.motorSpeed * (distanceMultiplier)
                    );
                }
                else
                {
                    lastMotorSpeed2 = Mathf.Abs(spiderController.motorSpeed * (distanceMultiplier));
                }
            }
            else
            {
                if (currentTarget.x < GetFootPosition().x)
                {
                    lastMotorSpeed2 = Mathf.Abs(spiderController.motorSpeed * (distanceMultiplier));
                }
                else
                {
                    lastMotorSpeed2 = -Mathf.Abs(
                        spiderController.motorSpeed * (distanceMultiplier)
                    );
                }
            }
        }
        else
        {
            // If further, slow down or stop
            if (lockTimeoutTimer > spiderController.lockTimeout)
            {
                lockTimer = 0f;
                lockTimeoutTimer = 0f;
            }

            lastMotorSpeed2 = Mathf.Clamp(
                spiderController.motorSpeed * (distanceMultiplier) * (lastMotorSpeed2 > 0 ? -1 : 1),
                -spiderController.maxMotorTorque,
                spiderController.maxMotorTorque
            ); // Reduce the speed
            // if (Mathf.Abs(lastMotorSpeed2) < 50f)
            // {
            //     lastMotorSpeed2 = 50; // Stop if speed is very low
            // }
        }
        lastMotorSpeedAgain = lastMotorSpeed;
        previousDistanceToTarget2 = distanceToTarget;

        // if(currentTarget.y < legSegment1.position.y && GetFootPosition().y > legSegment1.position.y){
        //     if(isLeftSide){
        //         lastMotorSpeed2 = Mathf.Abs(direction);
        //     } else{
        //         lastMotorSpeed2 = -Mathf.Abs(direction);
        //     }
        // }
        // if (hinge2HitLimit)
        // {
        //     lastMotorSpeed2 = -lastMotorSpeed2;
        // }
        lastAngle = hingeSegment1.jointAngle;

        return lastMotorSpeed2;
    }

    void LockLeg()
    {
        // Set motor speed to zero to lock the leg in place
        // SetMotorSpeed(hingeSegment1, 0);
        // SetMotorSpeed(hingeSegment2, 0);
        SetMotorSpeed(footHinge, 0);
        hingeSegment1.useMotor = false;
    }

    void SetMotorSpeed(HingeJoint2D hinge, float speed)
    {
        JointMotor2D motor = hinge.motor;
        // if (motor.motorSpeed == speed)
        //     return;
        motor.motorSpeed = speed * spiderController.motorSpeedMultiplier;
        motor.maxMotorTorque = spiderController.maxMotorTorque;
        hinge.motor = motor;
        hinge.useMotor = true;
    }

    void ActivateFootHinge(Vector2 point, Vector2 attachNormal)
    {
        float targetAngle = Mathf.Atan2(attachNormal.y, attachNormal.x) * Mathf.Rad2Deg - 90f; // Subtract 90 degrees to make it perpendicular
        //     float currentAngle = legSegment2.eulerAngles.z;
        //  targetPerpAngle = targetAngle- 90f;
        // legSegment2.rotation = Quaternion.Euler(0, 0, targetPerpAngle);
        //             float normalAngle = Mathf.DeltaAngle(legSegment2.eulerAngles.z, targetAngle) ;
        //         float minLimit = -45f + normalAngle; // Adjust these values based on your requirements
        //         float maxLimit = 45f + normalAngle;
        // Debug.DrawLine(currentTarget, currentNormal, Color.red);
        //         JointAngleLimits2D limits = footHinge.limits;
        //         limits.min = minLimit;
        //         limits.max = maxLimit;
        //         footHinge.limits = limits;
        footHinge.connectedBody = null;
        footHinge.enabled = true;
 // Adjust as necessary
    Collider2D[] hitColliders = Physics2D.OverlapCircleAll(footHinge.transform.position, searchRadius, LayerMask.GetMask("Default", "Web"));

    Rigidbody2D closestBody = null;
    float closestDistance = float.MaxValue;

    foreach (var hitCollider in hitColliders)
    {
        Rigidbody2D rb = hitCollider.attachedRigidbody;
        if (rb != null)
        {
            float distance = Vector2.Distance(footHinge.transform.position, rb.position);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closestBody = rb;
            }
        }
    }

    if (closestBody != null)
    {
        // Attach to the closest body
        footHinge.connectedBody = closestBody;
    }
        // Smoothly rotate legSegment2 to the desired angle

        // footHinge.useMotor = false;
        nextStepAssigned = false;
        isAttached = true;
        isWalking = false;
        // hingeSegment1.useMotor = false;

        // if(gameObject.tag == "Centipede") return;
        // JointAngleLimits2D limits = hingeSegment2.limits;
        // limits.min = hingeSegment2.jointAngle - 90f;
        // limits.max = hingeSegment2.jointAngle + 90f;
        // hingeSegment2.limits = limits;
    }

    void CheckForceOnFootJoint()
    {
        // Calculate the force being applied to the foot joint
        Vector2 relativeVelocity =
            segment2.GetPointVelocity(footHinge.transform.position)
            - spiderController.headRb.GetPointVelocity(footHinge.transform.position);
        float force = relativeVelocity.magnitude * segment2.mass;
        Debug.Log(gameObject.name + " " + force);
        // If the force exceeds the threshold, detach the foot joint
        // if (force > forceThreshold)
        // {
        //     DetachFootJoint();
        // }
    }

    public void ReleaseFootHinge()
    {
        if (isAttached)
        {
            footHinge.connectedBody = null;
            footHinge.enabled = false;
            isAttached = false;
        }
    }

    public void Detach()
    {
        footHinge.connectedBody = null;
        footHinge.enabled = false;
        isAttached = false;
        isWalking = true;

        raiseTimer = 0;
        SetNewTarget();
    }

    public AttachmentPoint GetFurthestPointInDirection(
        List<AttachmentPoint> points,
        Vector2 direction,
        float maxDistance
    )
    {
        if(gameObject.tag == "Centipede"){
            return GetFurthestPointInDirectionCenti(gameObject.GetComponent<CentipedeRaycaster>().points, direction, float.MaxValue);
        }
        Vector2 knee = legSegment1.transform.TransformPoint(hingeSegment2.connectedAnchor);
        AttachmentPoint furthestPoint = new AttachmentPoint();
        float maxDotProduct = float.MinValue;

        foreach (var AP in points)
        {
            Vector2 point = AP.point;
            float dotProduct = Vector2.Dot(direction, point);

            // Check if the point is within the maximum distance
            if (
                Vector2.Distance(GetFootPosition(), point) <= maxDistance
                && dotProduct > maxDotProduct
                && Vector2.Distance(head.position, point) < GetCombinedLegLength()
                && Vector2.Distance(head.position, point) > spiderController.headSafeArea
                && Vector2.Distance(knee, point) > segmentLength * .9f
            )
            {
                maxDotProduct = dotProduct;
                furthestPoint = AP;
            }
        }

        return furthestPoint;
    }
    public AttachmentPoint GetFurthestPointInDirectionCenti(
        List<AttachmentPoint> points,
        Vector2 direction,
        float maxDistance
    )
    {
        Vector2 knee = legSegment1.transform.TransformPoint(hingeSegment2.connectedAnchor);
        AttachmentPoint furthestPoint = new AttachmentPoint();
        float maxDotProduct = float.MinValue;

        foreach (var AP in points)
        {
            Vector2 point = AP.point;
            float dotProduct = Vector2.Dot(direction, point);

            // Check if the point is within the maximum distance
            if (
               dotProduct > maxDotProduct
            )
            {
                maxDotProduct = dotProduct;
                furthestPoint = AP;
            }
        }

        return furthestPoint;
    }
    public Vector2 GetClosestPointWithinDistance(
        List<Vector2> points,
        Vector2 headPosition,
        float footToPointDistance
    )
    {
        Vector2 closestPoint = Vector2.zero;
        float minDistance = float.MaxValue;

        foreach (var point in points)
        {
            float distance = Vector2.Distance(headPosition, point);

            // Calculate the relative position of the point in the head's local space
            Vector2 localPoint = head.InverseTransformPoint(point);

            // Check if the point is not directly above or below the head by ensuring a minimum X displacement
            if (
                Mathf.Abs(localPoint.x) > spiderController.headSafeArea * 0.5f // This checks if the point is not too close to the vertical axis
                && Vector2.Distance(GetFootPosition(), point) <= footToPointDistance
                && distance < minDistance
            )
            {
                minDistance = distance;
                closestPoint = point;
            }
        }

        return closestPoint;
    }

    public AttachmentPoint GetClosestPoint(
        List<AttachmentPoint> points,
        Vector2 footPosition,
        float maxDistance = 0
    )
    {
        Vector2 knee = legSegment1.transform.TransformPoint(hingeSegment2.connectedAnchor);
        AttachmentPoint closestPoint = new AttachmentPoint();
        float minDistance = float.MaxValue;
        maxDistance = maxDistance == 0 ? GetCombinedLegLength() : maxDistance;

        foreach (var AP in points)
        {
            Vector2 point = AP.point;
            float distance = Vector2.Distance(footPosition, point);

            // Calculate the relative position of the point in the head's local space
            Vector2 localPoint = head.InverseTransformPoint(point);

            // Ensure the point is not directly above or below the head and meets other conditions
            if (
                Mathf.Abs(localPoint.x) > spiderController.headSafeArea * 0.5f // This checks if the point is not too close to the vertical axis
                && distance < minDistance
                && Vector2.Distance(head.position, point) < maxDistance
                && Vector2.Distance(head.position, point) > spiderController.headSafeArea
                && Vector2.Distance(knee, point) > segmentLength * .9f
            )
            {
                minDistance = distance;
                closestPoint = AP;
            }
        }

        return closestPoint;
    }
void SetNewTargetCenti(){
AttachmentPoint bestPoint = new AttachmentPoint();
            float bestScore = float.MinValue;
            PerformRaycasts();
            if (
                isWalking && !nextStepAssigned
                || inputDirection != previousInputDirection
                || Vector2.Distance(GetFootPosition(), currentTarget)
                    > spiderController.legSightDistance
            )
            {
                
                attachmentPoints.AddRange(footAttachmentPoints);
                bestPoint = GetFurthestPointInDirection(
                    attachmentPoints,
                    inputDirection,
                    spiderController.legSightDistance
                );
                nextStepAssigned = true;
                previousInputDirection = inputDirection;
            }
            else if (!nextStepAssigned)
            {
                attachmentPoints.AddRange(footAttachmentPoints);
                bestPoint = GetClosestPoint(attachmentPoints, GetFootPosition());
                nextStepAssigned = true;
            }

            if (bestPoint.point != Vector2.zero)
            {
                lastTarget = currentTarget;
                currentTarget = bestPoint.point;
                currentNormal = bestPoint.normal;
            }
            else if (nextStepAssigned)
            {
                // Debug.Log("step already assigned");
            }
            else
            {
                Debug.Log("no target found");
            }
}
    public void SetNewTarget()
    {

        // if(gameObject.tag == "Centipede"){
        //     SetNewTargetCenti();
        // }
        if (attachmentPoints != null && attachmentPoints.Count > 0)
        {
            AttachmentPoint bestPoint = new AttachmentPoint();
            float bestScore = float.MinValue;
            PerformRaycasts();
            if (
                isWalking && !nextStepAssigned
                || inputDirection != previousInputDirection
                || Vector2.Distance(GetFootPosition(), currentTarget)
                    > spiderController.legSightDistance
            )
            {
                
                attachmentPoints.AddRange(footAttachmentPoints);
                bestPoint = GetFurthestPointInDirection(
                    attachmentPoints,
                    inputDirection,
                    spiderController.legSightDistance
                );
                nextStepAssigned = true;
                previousInputDirection = inputDirection;
            }
            else if (!nextStepAssigned)
            {
                attachmentPoints.AddRange(footAttachmentPoints);
                bestPoint = GetClosestPoint(attachmentPoints, GetFootPosition());
                nextStepAssigned = true;
            }

            if (bestPoint.point != Vector2.zero)
            {
                lastTarget = currentTarget;
                currentTarget = bestPoint.point;
                currentNormal = bestPoint.normal;
            }
            else if (nextStepAssigned)
            {
                // Debug.Log("step already assigned");
            }
            else
            {
                Debug.Log("no target found");
            }
        }
    }

    // public Vector2 GetFootPosition()
    // {
    //     float legLength = legSegment2.localScale.x + 2.3f;
        
    //     Vector2 footPosition =
    //         legSegment2.transform.TransformPoint(legSegment2.GetComponents<HingeJoint2D>()[1].anchor);
    //     return footPosition;
    // }
    public Vector2 GetFootPosition()
    {

        

        Vector2 footPosition =
            (Vector2)legSegment2.position + (Vector2)legSegment2.right * segmentLength;
        return footPosition;
    }
    public void SetInputDirection(Vector2 direction)
    {
        // Vector2 localInputDirection = head.InverseTransformDirection(inputDirection);
        // if(direction != localInputDirection){
        //     Debug.Log("direction changed from "+ direction + " to "  + localInputDirection);
        // }
        inputDirection = direction;
    }

    public void SetAttachmentPoints(List<AttachmentPoint> points)
    {
        // if (footAttachmentPoints.Count > 0)
        // {
        //     return;
        // }
        // attachmentPoints = points; // Ensure a new list to prevent unintended modification
        if(gameObject.tag == "Centipede"){
            return; // Ensure a new list to prevent unintended modification
        }
        else if (isLeftSide)
            attachmentPoints = spiderController.leftAttachmentPoints; // Ensure a new list to prevent unintended modification
        else
        {
            attachmentPoints = spiderController.rightAttachmentPoints; // Ensure a new list to prevent unintended modification
        }
    }

    private float GetCombinedLegLength()
    {
        return spiderController.maxLegExtension;
        // return 13.5f;
    }

    float limiter(float targetAngle, JointAngleLimits2D limits)
    {
        if (targetAngle < limits.min)
        {
            return limits.min;
        }
        else if (targetAngle > limits.max)
        {
            return limits.max;
        }
        return targetAngle;
    }

    float CalculateAngle(Vector3 start, Vector3 end)
    {
        Vector3 diff = end - start;
        return Mathf.Atan2(diff.y, diff.x) * Mathf.Rad2Deg;
    }

    public void PullFootToTarget()
    {
                
        if (currentTarget != Vector2.zero && !isAttached)
        {

            Vector2 footPosition = GetFootPosition();
            Vector2 directionToTarget = (currentTarget - footPosition).normalized;
            float aimRotation = 0f;
            float targetAngle =
                Mathf.Atan2(directionToTarget.y, directionToTarget.x) * Mathf.Rad2Deg;

            Quaternion targetRotation = Quaternion.Euler(0, 0, targetAngle);
            float currentAngle = segment2.transform.eulerAngles.z;

            // float rotationSpeed = spiderController.rotationSpeed * Time.deltaTime; // Adjust the rotation speed as needed
             distanceToTarget = Mathf.DeltaAngle(currentAngle, targetAngle);
            float rotationSpeed =

                    spiderController.rotationSpeed * Time.deltaTime;
                
            float newAngle = Mathf.MoveTowardsAngle(currentAngle, targetAngle, rotationSpeed);
                        if(!segment2.freezeRotation){
                // legSegment2.transform.rotation = Quaternion.Euler(0, 0, newAngle);
                segment2.MoveRotation(Mathf.LerpAngle(segment2.rotation,targetAngle, rotationSpeed));
            }else{
                segment2.MoveRotation(Mathf.LerpAngle(segment2.rotation,currentAngle, rotationSpeed));
            }
            

            RaycastHit2D hit = Physics2D.Raycast(
                footPosition,
                directionToTarget,
                spiderController.legSightDistance,
                attachableLayers
            );

            if (
                hit.collider != null
                && Vector2.Distance(hit.point, currentTarget) <= spiderController.footPullTolerance
                && raiseTimer > raiseTime
                
            )
            {
                useMotor1 = false;
                if (spiderController.freezeLeg2Rotation)
                    segment2.freezeRotation = true;
                aimAngle = legSegment2.transform.eulerAngles.z;
                float forceMagnitude = spiderController.footMagnetForce; // Adjust the magnitude of the force as needed
                Vector2 force = directionToTarget * forceMagnitude;

                if (segment2 != null)
                {
                    if (spiderController.raiseTime < raiseTimer && spiderController.detached >= spiderController.maxDetached)
                        segment2.AddForce(force);
                    // Debug.Log("force applied");
                }
            }
            else
            {
                aimSet = false;
                useMotor1 = true;
                if(gameObject.tag != "Centipede")
                Leg1StepPrep();
                // segment2.freezeRotation = false;
            }
        }
    }

    
    void CalculateFootHingeMotorSpeed()
    {
        // Convert the input direction to the local space of the head
        Vector2 localInputDirection;
        if(gameObject.tag == "Centipede")

        localInputDirection = spiderController.head.InverseTransformDirection(inputDirection);
        else
        localInputDirection = head.InverseTransformDirection(inputDirection);
        localInputDirection.Normalize();

        // Determine the target direction for the foot hinge based on the input direction
        Vector2 targetDirection = head.TransformDirection(localInputDirection);

        // Calculate the target angle for the foot hinge in world space
        float targetAngle = Mathf.Atan2(targetDirection.y, targetDirection.x) * Mathf.Rad2Deg;

        // Get the current angle of legSegment2 in world space
        float currentAngle = legSegment2.eulerAngles.z;

        // Calculate the difference between the current angle and the target angle
        float angleDifference = Mathf.DeltaAngle(currentAngle, targetAngle);

        // Determine the motor speed based on the angle difference
        float motorSpeed = angleDifference * spiderController.footSpeed;

        // Set the motor speed for the foot hinge
        SetMotorSpeed(footHinge, motorSpeed);

        // Optionally disable the motor for hingeSegment2 to allow it to follow naturally
        hingeSegment2.useMotor = false;

        if (Mathf.Abs(localInputDirection.y) > Mathf.Abs(localInputDirection.x))
        {
            // The input is mostly vertical (up or down)
            float currentAngle1 = legSegment1.eulerAngles.z;

            // Calculate the angle for hingeSegment1
            float targetAngle1 = localInputDirection.y > 0 ? 90f : -90f; // Up or down

            // Calculate the difference between the current angle and the target angle
            float angleDifference1 = Mathf.DeltaAngle(currentAngle1, targetAngle1);

            // Determine the motor speed for hingeSegment1
            float motorSpeed1 = angleDifference1 * spiderController.footSpeed;

            // Apply the motor speed to hingeSegment1
            SetMotorSpeed(hingeSegment1, motorSpeed1);
        }
    }

    void FootDetachCheck()
    {
        Vector2 relativeVelocity =
            segment2.GetPointVelocity(footHinge.transform.position)
            - spiderController.headRb.GetPointVelocity(footHinge.transform.position);
        float force = relativeVelocity.magnitude * segment2.mass;
        if (
            footHinge.jointAngle < footHinge.limits.min + 5
            || footHinge.jointAngle > footHinge.limits.max - 5
        )
        {
            Detach();
            Debug.Log("FootDetachCheck");
        }
        else if (force > spiderController.forceThreshold)
        {
            Detach();
            Debug.Log(gameObject.name + " force threshold");
        }
        // Calculate the force being applied to the foot joint

        // If the force exceeds the threshold, detach the foot joint
        // if (force > forceThreshold)
        // {
        //     DetachFootJoint();
        // }
    }

    void OnDrawGizmos()
    {
            if (!Application.isPlaying)
    {
        return; // Exit the method if the editor is not playing
    }

        Gizmos.color = Color.red;
        Gizmos.DrawSphere(head.position, 0.1f); // Visualize the head

        // Visualize the base position
        Gizmos.DrawSphere(basePosition, 0.1f);

        // Visualize the current positions with rectangles
        Gizmos.color = Color.green;
        if(gameObject.tag == "Centipede" && legSegment1 == null){
                                legSegment1 =  transform.GetChild(0).transform;
        legSegment2 = transform.GetChild(1).transform;
        }
        
        Gizmos.color = hingeSegment2.reactionForce.magnitude > spiderController.forceThreshold ? Color.red : Color.green;
        DrawRectangle(
            legSegment2.position,
            segmentLength,
            segmentWidth,
            legSegment2.transform.eulerAngles.z
        );
        // DrawRectangle(
        //     legSegment1.position,
        //     segmentLength,
        //     segmentWidth,
        //     legSegment1.transform.eulerAngles.z
        // );
        // DrawRectangle(
        //     legSegment2.position,
        //     segmentLength,
        //     segmentWidth,
        //     legSegment2.transform.eulerAngles.z
        // );

        // Visualize the collision check line
        Vector2 footPosition = GetFootPosition();
        Vector2 directionToTarget = (currentTarget - footPosition).normalized;
        RaycastHit2D hit = Physics2D.Raycast(
            footPosition,
            directionToTarget,
            spiderController.legSightDistance,
            attachableLayers
        );

        if (hit.collider != null)
        {
            Gizmos.color = Color.red; // Collision detected
            Gizmos.DrawLine(footPosition, hit.point);
            Gizmos.DrawSphere(hit.point, 0.1f); // Mark the collision point
        }
        else
        {
            Gizmos.color = Color.green; // No collision
            Vector2 raycastEnd =
                footPosition + directionToTarget * spiderController.legSightDistance;
            Gizmos.DrawLine(footPosition, raycastEnd);
        }

        // Visualize the target position
        Gizmos.color = Color.blue;
        Gizmos.DrawSphere(currentTarget, 0.1f);

        // Additional Gizmo logic (if needed)
        // ...
    }

    // Helper function to draw a rectangle representing the leg segments
    void DrawRectangle(Vector3 position, float length, float width, float angle)
    {
        Vector3 offset = new Vector3(length / 2, 0, 0);
        Quaternion rotation = Quaternion.Euler(0, 0, angle);

        Vector3 p1 = position + rotation * (offset + new Vector3(0, width / 2, 0));
        Vector3 p2 = position + rotation * (offset - new Vector3(0, width / 2, 0));
        Vector3 p3 = position + rotation * (-offset - new Vector3(0, width / 2, 0));
        Vector3 p4 = position + rotation * (-offset + new Vector3(0, width / 2, 0));

        Gizmos.DrawLine(p1, p2);
        Gizmos.DrawLine(p2, p3);
        Gizmos.DrawLine(p3, p4);
        Gizmos.DrawLine(p4, p1);
    }
}
