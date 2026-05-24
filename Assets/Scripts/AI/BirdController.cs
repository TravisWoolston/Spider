using UnityEngine;

/// <summary>
/// Controls a profile-angle 2D bird with animated wing flapping,
/// lifelike head movement, and independent pupil tracking.
/// Head and eyes idle-scan the environment, and can lock onto a target.
/// </summary>
public class BirdController : MonoBehaviour
{
    // ── Enums ──────────────────────────────────────────────────
    public enum BirdState { Idle, Scanning, Locked }

    // ── Inspector: Hierarchy References ────────────────────────
    [Header("Body Parts")]
    [Tooltip("The head transform that rotates to look around.")]
    public Transform head;

    [Tooltip("The pupil transform that slides within the eye socket.")]
    public Transform pupil;

    [Tooltip("The eye socket transform, used as the pupil's center anchor.")]
    public Transform eyeSocket;

    [Header("Wing Animation")]
    [Tooltip("SpriteRenderer for the wing. Assign the wing child object.")]
    public SpriteRenderer wingRenderer;

    [Tooltip("Sprite shown when the bird is perched/grounded (not flying).")]
    public Sprite wingRestedSprite;

    [Tooltip("Ordered wing flap sprite frames for flying (e.g. down, mid, up). Last frame = fully extended up.")]
    public Sprite[] wingFrames;

    [Tooltip("Hold duration (seconds) for each wing frame. Shorter = faster snap, longer = lingering pose. Must match wingFrames length.")]
    public float[] frameHoldTimes;

    [Tooltip("Base speed multiplier for the entire flap cycle.")]
    public float flapSpeed = 1f;

    [Tooltip("Random variation applied to each flap cycle's speed (0 = perfectly mechanical, 0.2 = subtle life).")]
    [Range(0f, 0.5f)]
    public float flapSpeedVariation = 0.15f;

    [Tooltip("Extra random hold added to the last frame (wings fully up) to simulate catching air. Creates a natural hang at the top of each stroke.")]
    public float apexHoldMin = 0.02f;
    public float apexHoldMax = 0.15f;

    [Tooltip("Seconds to pause between flap cycles (wings held at apex). Set both to 0 for continuous flapping.")]
    public float glidePauseMin = 0f;
    public float glidePauseMax = 0.5f;

    [Tooltip("Whether the bird is currently flying. Controls wing animation vs rested sprite.")]
    public bool isFlying = true;

    // ── Inspector: Head Scanning ──────────────────────────────
    [Header("Head Scanning")]
    [Tooltip("Max degrees the head rotates from its rest pose during idle scanning.")]
    public float scanAngleMax = 25f;

    [Tooltip("Min degrees the head rotates from its rest pose during idle scanning.")]
    public float scanAngleMin = 5f;

    [Tooltip("How fast the head moves to a new scan angle (degrees/sec).")]
    public float scanSpeed = 60f;

    [Tooltip("Seconds the head pauses at each scan position before picking a new one.")]
    public float scanPauseMin = 0.3f;
    public float scanPauseMax = 1.5f;

    [Tooltip("How fast the head snaps to a locked target (degrees/sec).")]
    public float lockOnSpeed = 180f;

    // ── Inspector: Pupil ──────────────────────────────────────
    [Header("Pupil")]
    [Tooltip("Max distance the pupil can slide from the eye socket center.")]
    public float pupilRange = 0.15f;

    [Tooltip("How fast the pupil moves toward its target offset.")]
    public float pupilSpeed = 10f;

    // ── Inspector: Targeting ──────────────────────────────────
    [Header("Targeting")]
    [Tooltip("Optional target transform for the head and eyes to lock onto.")]
    public Transform lockTarget;

    [Tooltip("Max distance at which the bird will notice and lock onto a target.")]
    public float lockOnDistance = 15f;

    // ── Inspector: Facing ─────────────────────────────────────
    [Header("Facing")]
    [Tooltip("Set to 1 for right-facing sprite, -1 for left-facing.")]
    public float facingSign = 1f;

    // ── Inspector: Debug ──────────────────────────────────────
    [Header("Debug")]
    public bool showDebug = false;

    // ── Runtime State ─────────────────────────────────────────
    private BirdState currentState = BirdState.Scanning;
    private float flapTimer;
    private int currentFrame;
    private float currentCycleSpeed;
    private float glidePauseTimer;
    private bool isGlidePausing;
    private float currentApexHold;

    // Scanning
    private float targetScanAngle;
    private float currentScanAngle;
    private float scanPauseTimer;
    private bool isWaitingAtScanPose;
    private Quaternion headRestRotation;

    // Pupil
    private Vector3 pupilRestLocalPos;

    // ── Lifecycle ─────────────────────────────────────────────

    private void Start()
    {
        if (head != null)
            headRestRotation = head.localRotation;

        if (pupil != null)
            pupilRestLocalPos = pupil.localPosition;

        PickNewScanAngle();
        PickNewCycleSpeed();
    }

    private void Update()
    {
        UpdateState();
        UpdateWingAnimation();
        UpdateHead();
        UpdatePupil();
    }

    // ── State Management ──────────────────────────────────────

    /// <summary>
    /// Determines the current bird state each frame.
    /// </summary>
    private void UpdateState()
    {
        if (lockTarget != null)
        {
            float dist = Vector2.Distance(transform.position, lockTarget.position);
            currentState = dist <= lockOnDistance ? BirdState.Locked : BirdState.Scanning;
        }
        else
        {
            currentState = BirdState.Scanning;
        }
    }

    // ── Wing Animation ────────────────────────────────────────

    /// <summary>
    /// Routes wing visuals based on flying state.
    /// When grounded, shows the rested sprite. When flying, runs the flap cycle.
    /// </summary>
    private void UpdateWingAnimation()
    {
        if (wingRenderer == null)
            return;

        if (!isFlying)
        {
            ShowRestedWing();
            return;
        }

        UpdateFlapCycle();
    }

    /// <summary>
    /// Displays the rested wing sprite and resets the flap cycle
    /// so it starts cleanly when the bird takes off.
    /// </summary>
    private void ShowRestedWing()
    {
        if (wingRestedSprite != null)
            wingRenderer.sprite = wingRestedSprite;

        currentFrame = 0;
        flapTimer = 0f;
        isGlidePausing = false;
    }

    /// <summary>
    /// Advances the flap cycle using per-frame hold durations,
    /// with random apex hold variation and glide pauses between cycles.
    /// </summary>
    private void UpdateFlapCycle()
    {
        if (wingFrames == null || wingFrames.Length == 0)
            return;

        // Glide pause between flap cycles (held at apex frame)
        if (isGlidePausing)
        {
            glidePauseTimer -= Time.deltaTime;
            if (glidePauseTimer <= 0f)
            {
                isGlidePausing = false;
                PickNewCycleSpeed();
            }
            return;
        }

        bool isApexFrame = currentFrame == wingFrames.Length - 1;
        float holdTime = GetFrameHoldTime(currentFrame) / currentCycleSpeed;

        // Add random extra hold on the last frame (wings fully extended up)
        if (isApexFrame)
            holdTime += currentApexHold;

        flapTimer += Time.deltaTime;

        if (flapTimer >= holdTime)
        {
            flapTimer -= holdTime;
            currentFrame++;

            // End of cycle
            if (currentFrame >= wingFrames.Length)
            {
                currentFrame = 0;
                PickNewApexHold();

                float pause = Random.Range(glidePauseMin, glidePauseMax);
                if (pause > 0f)
                {
                    isGlidePausing = true;
                    glidePauseTimer = pause;
                }
                else
                {
                    PickNewCycleSpeed();
                }
            }

            wingRenderer.sprite = wingFrames[currentFrame];
        }
    }

    /// <summary>
    /// Returns the hold duration for the given frame index.
    /// Falls back to a uniform default if frameHoldTimes is not configured.
    /// </summary>
    private float GetFrameHoldTime(int frameIndex)
    {
        const float defaultHoldTime = 0.1f;

        if (frameHoldTimes == null || frameHoldTimes.Length == 0)
            return defaultHoldTime;

        if (frameIndex < frameHoldTimes.Length)
            return frameHoldTimes[frameIndex];

        return frameHoldTimes[frameHoldTimes.Length - 1];
    }

    /// <summary>
    /// Randomizes the speed multiplier for the next flap cycle.
    /// </summary>
    private void PickNewCycleSpeed()
    {
        currentCycleSpeed = flapSpeed * (1f + Random.Range(-flapSpeedVariation, flapSpeedVariation));
        currentCycleSpeed = Mathf.Max(currentCycleSpeed, 0.1f);
    }

    /// <summary>
    /// Picks a random extra hold duration for the apex (wings fully up) frame.
    /// </summary>
    private void PickNewApexHold()
    {
        currentApexHold = Random.Range(apexHoldMin, apexHoldMax);
    }

    // ── Head Movement ─────────────────────────────────────────

    /// <summary>
    /// Routes head behavior based on current state.
    /// </summary>
    private void UpdateHead()
    {
        if (head == null)
            return;

        switch (currentState)
        {
            case BirdState.Scanning:
            case BirdState.Idle:
                UpdateHeadScanning();
                break;
            case BirdState.Locked:
                UpdateHeadLockOn();
                break;
        }
    }

    /// <summary>
    /// Idle scanning: the head rotates to random angles with natural pauses,
    /// mimicking how a bird surveys its surroundings.
    /// </summary>
    private void UpdateHeadScanning()
    {
        if (isWaitingAtScanPose)
        {
            scanPauseTimer -= Time.deltaTime;
            if (scanPauseTimer <= 0f)
            {
                isWaitingAtScanPose = false;
                PickNewScanAngle();
            }
            return;
        }

        currentScanAngle = Mathf.MoveTowards(
            currentScanAngle,
            targetScanAngle,
            scanSpeed * Time.deltaTime
        );

        ApplyHeadAngle(currentScanAngle);

        if (Mathf.Abs(currentScanAngle - targetScanAngle) < 0.5f)
        {
            isWaitingAtScanPose = true;
            scanPauseTimer = Random.Range(scanPauseMin, scanPauseMax);
        }
    }

    /// <summary>
    /// Locked: the head rotates to face the lock target.
    /// </summary>
    private void UpdateHeadLockOn()
    {
        if (lockTarget == null)
            return;

        Vector2 toTarget = lockTarget.position - head.position;
        float angleToTarget = Mathf.Atan2(toTarget.y, toTarget.x * facingSign) * Mathf.Rad2Deg;
        angleToTarget = Mathf.Clamp(angleToTarget, -scanAngleMax * 2f, scanAngleMax * 2f);

        currentScanAngle = Mathf.MoveTowards(
            currentScanAngle,
            angleToTarget,
            lockOnSpeed * Time.deltaTime
        );

        ApplyHeadAngle(currentScanAngle);
    }

    /// <summary>
    /// Applies a Z-rotation offset to the head relative to its rest pose.
    /// </summary>
    private void ApplyHeadAngle(float angle)
    {
        head.localRotation = headRestRotation * Quaternion.Euler(0f, 0f, angle);
    }

    /// <summary>
    /// Picks a new random scan angle, biased forward (smaller angles more likely).
    /// </summary>
    private void PickNewScanAngle()
    {
        // Bias toward smaller angles to simulate forward-focused scanning
        float range = Random.Range(scanAngleMin, scanAngleMax);
        targetScanAngle = Random.Range(-range, range);

        // Occasionally do a bigger "check behind" sweep
        if (Random.value < 0.15f)
            targetScanAngle = Random.Range(-scanAngleMax, scanAngleMax);
    }

    // ── Pupil Tracking ────────────────────────────────────────

    /// <summary>
    /// Moves the pupil within the eye socket to follow the head direction
    /// or lock target, creating the illusion of a living eye.
    /// </summary>
    private void UpdatePupil()
    {
        if (pupil == null || eyeSocket == null)
            return;

        Vector2 targetOffset;

        if (currentState == BirdState.Locked && lockTarget != null)
        {
            // Pupil looks directly at the target
            Vector2 toTarget = lockTarget.position - eyeSocket.position;
            targetOffset = toTarget.normalized * pupilRange;

            // Convert to local space of the eye socket
            targetOffset = eyeSocket.InverseTransformDirection(targetOffset);
        }
        else
        {
            // Pupil follows the head scan direction
            float scanNormalized = currentScanAngle / scanAngleMax;
            targetOffset = new Vector2(scanNormalized * pupilRange * facingSign, 0f);
        }

        // Clamp to pupil range
        targetOffset = Vector2.ClampMagnitude(targetOffset, pupilRange);

        Vector3 desiredLocalPos = pupilRestLocalPos + (Vector3)targetOffset;
        pupil.localPosition = Vector3.Lerp(
            pupil.localPosition,
            desiredLocalPos,
            pupilSpeed * Time.deltaTime
        );
    }

    // ── Public API ────────────────────────────────────────────

    /// <summary>
    /// Sets a transform for the head and eyes to track.
    /// Pass null to return to idle scanning.
    /// </summary>
    public void SetLockTarget(Transform target)
    {
        lockTarget = target;
    }

    /// <summary>
    /// Flips the bird's facing direction.
    /// </summary>
    public void SetFacing(float sign)
    {
        facingSign = Mathf.Sign(sign);
        Vector3 scale = transform.localScale;
        scale.x = Mathf.Abs(scale.x) * facingSign;
        transform.localScale = scale;
    }

    /// <summary>
    /// Returns the current bird state.
    /// </summary>
    public BirdState GetState()
    {
        return currentState;
    }

    // ── Debug ─────────────────────────────────────────────────

    private void OnDrawGizmosSelected()
    {
        if (!showDebug)
            return;

        // Lock-on detection radius
        Gizmos.color = new Color(1f, 0.3f, 0.3f, 0.2f);
        Gizmos.DrawWireSphere(transform.position, lockOnDistance);

        // Scan cone visualization
        if (head != null)
        {
            Gizmos.color = Color.yellow;
            Vector3 forward = head.right * facingSign;
            Vector3 upper = Quaternion.Euler(0, 0, scanAngleMax) * forward;
            Vector3 lower = Quaternion.Euler(0, 0, -scanAngleMax) * forward;
            float coneLength = 5f;
            Gizmos.DrawLine(head.position, head.position + upper * coneLength);
            Gizmos.DrawLine(head.position, head.position + lower * coneLength);
        }

        // Pupil range
        if (eyeSocket != null)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(eyeSocket.position, pupilRange);
        }
    }
}
