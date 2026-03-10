using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Optimized spider body controller. Manages raycasting, input, head alignment,
/// and urgency-based gait coordination. Pair with SpiderLeg2 on each leg.
/// </summary>
public class SpiderController2 : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Transform head;
    [SerializeField] private SpiderLeg2[] legs;
    [SerializeField] private Rigidbody2D headRigidbody;

    [Header("Input")]
    [SerializeField] private bool isAI;

    [Header("Debug")]
    [SerializeField] private bool showDebug;

    [Header("Raycasting")]
    [Tooltip("Rays in a 360 sweep. 72 = one every 5 degrees.")]
    [SerializeField, Range(12, 360)] private int rayCount = 72;
    [SerializeField] private float sightDistance = 30f;

    [Header("Movement")]
    [SerializeField] private float maxMotorTorque = 1000f;
    [SerializeField] private float footSpeed = 200f;
    [SerializeField] private float rotationSpeed = 20f;
    [SerializeField] private float headAlignSpeed = 5f;

    [Header("Leg Constraints")]
    [SerializeField] private float maxLegExtension = 14f;
    [SerializeField] private float headSafeArea = 5f;
    [SerializeField] private float attachRadius = 3f;
    [SerializeField] private float searchRadius = 5f;
    [SerializeField] private float magnetForce = 200f;

    [Header("Gait")]
    [Tooltip("Minimum urgency before a leg will step.")]
    [SerializeField, Range(0f, 3f)] private float urgencyThreshold = 0.5f;
    [Tooltip("Max legs in-flight simultaneously.")]
    [SerializeField, Range(1, 6)] private int maxInFlight = 2;
    [Tooltip("Seconds for a step arc to complete.")]
    [SerializeField] private float stepDuration = 0.3f;
    [Tooltip("Arc height as fraction of leg length.")]
    [SerializeField, Range(0f, 1f)] private float stepArcHeight = 0.25f;
    [Tooltip("Seconds before a mid-step target is reassigned.")]
    [SerializeField] private float targetReassignTime = 1.5f;

    // Runtime state
    private Vector2 inputDirection;
    private readonly List<AttachmentPoint> leftPoints = new();
    private readonly List<AttachmentPoint> rightPoints = new();
    private int cachedDetached, cachedDetachedL, cachedDetachedR;
    private bool lockInput;

    // Read-only accessors for SpiderLeg2
    public Transform Head => head;
    public Rigidbody2D HeadRb => headRigidbody;
    public Vector2 InputDirection => inputDirection;
    public float MaxMotorTorque => maxMotorTorque;
    public float FootSpeed => footSpeed;
    public float RotationSpeed => rotationSpeed;
    public float MaxLegExtension => maxLegExtension;
    public float HeadSafeArea => headSafeArea;
    public float AttachRadius => attachRadius;
    public float SearchRadius => searchRadius;
    public float MagnetForce => magnetForce;
    public float StepDuration => stepDuration;
    public float StepArcHeight => stepArcHeight;
    public float TargetReassignTime => targetReassignTime;
    public int DetachedTotal => cachedDetached;
    public SpiderLeg2[] Legs => legs;
    public bool ShowDebug => showDebug;

    void Awake()
    {
        // Auto-find head
        if (head == null)
        {
            var headFind = transform.Find("Spider Head");
            if (headFind == null) headFind = transform.GetComponentInChildren<Rigidbody2D>()?.transform; // fallback
            head = headFind;
        }

        // Auto-find head rb
        if (headRigidbody == null && head != null)
            headRigidbody = head.GetComponent<Rigidbody2D>();

        // Auto-find all legs
        if (legs == null || legs.Length == 0)
            legs = GetComponentsInChildren<SpiderLeg2>(true);
    }

    void Start()
    {
        PerformRaycasts();
        DistributePoints();
        foreach (var leg in legs)
            leg.FindInitialTarget();
    }

    void FixedUpdate()
    {
        PerformRaycasts();
        CacheDetachedCounts();
        AlignHeadToSurface();
    }

    void Update()
    {
        if (!isAI) ReadInput();
        DistributePoints();

        if (Input.GetKey(KeyCode.Space))
            ReleaseAllFeet();

        if (inputDirection != Vector2.zero)
            DecideSteps();
    }

    // ─── Input ───────────────────────────────────────────────

    public void SetInputDirection(Vector2 dir) => inputDirection = dir;

    private void ReadInput()
    {
        if (Input.GetKeyDown(KeyCode.F))
            lockInput = !lockInput;

        if (!lockInput)
        {
            inputDirection = new Vector2(
                Input.GetAxis("Horizontal"),
                Input.GetAxis("Vertical")
            ).normalized;
        }
    }

    // ─── Raycasting (single merged pass) ─────────────────────

    private void PerformRaycasts()
    {
        leftPoints.Clear();
        rightPoints.Clear();

        float angleStep = 360f / rayCount;
        LayerMask mask = LayerMask.GetMask("Default", "Web");

        for (int i = 0; i < rayCount; i++)
        {
            float angle = i * angleStep;
            Vector2 dir = new(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad));

            var hits = Physics2D.RaycastAll(head.position, dir, sightDistance, mask);
            foreach (var hit in hits)
            {
                if (hit.collider == null) continue;
                var ap = new AttachmentPoint(hit.point, hit.normal);
                Vector2 local = head.InverseTransformPoint(hit.point);

                if (!isAI)
                    GlobalAttachmentManager.StoreAttachmentPoint(hit.point, hit.normal);

                if (local.x < 0) leftPoints.Add(ap);
                else             rightPoints.Add(ap);
            }
        }
    }

    // ─── Detach Counting (single pass) ───────────────────────

    private void CacheDetachedCounts()
    {
        cachedDetached = cachedDetachedL = cachedDetachedR = 0;
        foreach (var leg in legs)
        {
            if (leg.IsAttached) continue;
            cachedDetached++;
            if (leg.IsLeftSide) cachedDetachedL++;
            else                cachedDetachedR++;
        }
    }

    // ─── Attachment Point Distribution ───────────────────────

    private void DistributePoints()
    {
        foreach (var leg in legs)
            leg.SetAvailablePoints(leg.IsLeftSide ? leftPoints : rightPoints);
    }

    // ─── Urgency-Based Gait ──────────────────────────────────

    private void DecideSteps()
    {
        if (cachedDetached >= maxInFlight) return;

        SpiderLeg2 best = null;
        float bestUrgency = urgencyThreshold;
        int halfMax = Mathf.CeilToInt(maxInFlight / 2f);

        foreach (var leg in legs)
        {
            if (!leg.IsAttached) continue;

            // Don't overload one side
            if (leg.IsLeftSide && cachedDetachedL >= halfMax) continue;
            if (!leg.IsLeftSide && cachedDetachedR >= halfMax) continue;

            float u = leg.GetStepUrgency(inputDirection);
            if (u > bestUrgency && CanSafelyDetach(leg))
            {
                bestUrgency = u;
                best = leg;
            }
        }

        if (best != null)
            best.BeginStep(inputDirection);
    }

    private bool CanSafelyDetach(SpiderLeg2 candidate)
    {
        int remaining = 0;
        foreach (var leg in legs)
            if (leg != candidate && leg.IsAttached) remaining++;
        return remaining >= 2;
    }

    // ─── Head Alignment ──────────────────────────────────────

    private void AlignHeadToSurface()
    {
        Vector2[] dirs = {
            -head.up,
            Quaternion.Euler(0, 0, -45f) * head.up,
            Quaternion.Euler(0, 0,  45f) * head.up,
            head.up
        };

        foreach (var dir in dirs)
        {
            var hit = Physics2D.Raycast(head.position, dir, sightDistance, LayerMask.GetMask("Default"));
            if (hit.collider != null)
            {
                headRigidbody.freezeRotation = true;
                float target = Mathf.Atan2(hit.normal.y, hit.normal.x) * Mathf.Rad2Deg - 90f;
                head.rotation = Quaternion.Slerp(
                    head.rotation,
                    Quaternion.Euler(0, 0, target),
                    Time.fixedDeltaTime * headAlignSpeed
                );
                return;
            }
        }
        headRigidbody.freezeRotation = false;
    }

    // ─── Utilities ───────────────────────────────────────────

    private void ReleaseAllFeet()
    {
        foreach (var leg in legs) leg.ReleaseFoot();
    }

    /// <summary>Returns minimum distance from point to any other grounded foot.</summary>
    public float MinDistToOtherFeet(Vector2 point, SpiderLeg2 exclude)
    {
        float min = float.MaxValue;
        foreach (var leg in legs)
        {
            if (leg == exclude || !leg.IsAttached) continue;
            float d = Vector2.Distance(point, leg.FootPosition);
            if (d < min) min = d;
        }
        return min;
    }

    // ─── Debug Gizmos ────────────────────────────────────────

    void OnDrawGizmos()
    {
        if (!showDebug || !Application.isPlaying || head == null) return;

        // Head safe area (yellow) and max leg reach (cyan)
        Gizmos.color = new Color(1f, 1f, 0f, 0.3f);
        DrawCircle((Vector2)head.position, headSafeArea, 24);
        Gizmos.color = new Color(0f, 1f, 1f, 0.15f);
        DrawCircle((Vector2)head.position, maxLegExtension, 36);

        // Attachment points: green = left, red = right
        Gizmos.color = new Color(0.2f, 1f, 0.2f, 0.4f);
        foreach (var ap in leftPoints)
            Gizmos.DrawSphere(ap.point, 0.15f);
        Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.4f);
        foreach (var ap in rightPoints)
            Gizmos.DrawSphere(ap.point, 0.15f);

        // Input direction arrow (white)
        if (inputDirection != Vector2.zero)
        {
            Gizmos.color = Color.white;
            Vector2 from = head.position;
            Vector2 to = from + inputDirection * 3f;
            Gizmos.DrawLine(from, to);
            Gizmos.DrawSphere(to, 0.2f);
        }
    }

    private static void DrawCircle(Vector2 center, float radius, int segments)
    {
        float step = 360f / segments;
        for (int i = 0; i < segments; i++)
        {
            float a1 = i * step * Mathf.Deg2Rad;
            float a2 = (i + 1) * step * Mathf.Deg2Rad;
            Vector2 p1 = center + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * radius;
            Vector2 p2 = center + new Vector2(Mathf.Cos(a2), Mathf.Sin(a2)) * radius;
            Gizmos.DrawLine(p1, p2);
        }
    }
}
