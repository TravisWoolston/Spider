using UnityEngine;
using System.Collections;

public class GridCameraController : MonoBehaviour
{
    public Rigidbody2D targetRb; // Reference to the target's rigidbody
    public Vector3 offset; // Offset from the target

    private Camera cam;
    private Vector3 initialPosition;
    public float camMoveMod = 2f;
    public float transitionMod = 0f;
    public float transitionModStore;
    public bool disableTransition;
    public Vector3 targetPosition; // Target position for the camera
    public float transitionSpeed = 2f;
    public float camZoom = .1f;
    public bool followTarget = false;
    public float followSmoothing = 5f;
    
    [Header("Zoom Settings")]
    public float scrollSensitivity = 15f;
    public float minZoom = 2f;
    public float maxZoom = 40f;

    private float GetCurrentZoom()
    {
        return cam.orthographic ? cam.orthographicSize : cam.fieldOfView;
    }

    private void SetCurrentZoom(float value)
    {
        if (cam.orthographic)
            cam.orthographicSize = Mathf.Clamp(value, minZoom, maxZoom);
        else
            cam.fieldOfView = Mathf.Clamp(value, minZoom, maxZoom);
    }

    private float GetCameraHalfHeightAtTarget()
    {
        if (cam.orthographic)
            return cam.orthographicSize;

        if (targetRb == null)
            return 1f;

        float depth = Vector3.Dot(targetRb.transform.position - cam.transform.position, cam.transform.forward);
        depth = Mathf.Max(0.01f, depth);
        return Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * depth;
    }

    void Start()
    {
        cam = GetComponentInChildren<Camera>();

        if (targetRb == null)
        {
            Debug.LogWarning("Target Rigidbody not assigned to GridCameraController. Trying to find one on this object...");
            targetRb = GetComponent<Rigidbody2D>();
        }
        
        initialPosition = transform.position;
        transitionModStore = transitionMod;
        targetPosition = transform.position;
    }

    void LateUpdate()
    {
        if (cam == null) return;

        // Zoom Control
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        float zoom = GetCurrentZoom();
        if (scroll != 0)
        {
            zoom -= scroll * scrollSensitivity;
        }

        if (Input.GetKey(KeyCode.Keypad1))
        {
            zoom -= camZoom;
        }
        if (Input.GetKey(KeyCode.Keypad2))
        {
            zoom += camZoom;
        }

        SetCurrentZoom(zoom);

        if (targetRb == null) return;

        if (followTarget)
        {
            // Smoothly follow the target directly
            Vector3 goal = new Vector3(targetRb.position.x, targetRb.position.y, transform.position.z) + offset;
            transform.position = Vector3.Lerp(transform.position, goal, Time.deltaTime * followSmoothing);
            targetPosition = transform.position; // Keep targetPosition synced for when toggle is turned off
        }
        else
        {
            // Existing Grid-based movement logic
            Vector3 viewportPoint = cam.WorldToViewportPoint(targetRb.position);
            if (disableTransition)
            {
                transitionMod = 0;
            }
            else
            {
                transitionMod = transitionModStore;
            }

            if (Mathf.Floor(transform.position.x) == Mathf.Floor(targetPosition.x) && viewportPoint.x > 0 + transitionModStore + .15f && viewportPoint.x < 1 - transitionModStore - .15f)
            {
                disableTransition = false;
                transitionMod = transitionModStore;
            }

            if (Mathf.Floor(transform.position.x) == Mathf.Floor(targetPosition.x) && (viewportPoint.x < 0 || viewportPoint.x > 1))
            {
                disableTransition = false;
                transitionMod = transitionModStore;
                targetPosition.y = targetRb.position.y;
            }

            if ((viewportPoint.x < 0 + transitionMod || viewportPoint.x > 1 - transitionMod) && !disableTransition)
            {
                float camHalfHeight = GetCameraHalfHeightAtTarget();
                float moveAmountX = camHalfHeight * cam.aspect * (camMoveMod - transitionMod);
                if (viewportPoint.x < 0 + transitionMod)
                {
                    targetPosition -= new Vector3(moveAmountX, 0, 0);
                }
                else if (viewportPoint.x > 1 - transitionMod)
                {
                    targetPosition += new Vector3(moveAmountX, 0, 0);
                }
                disableTransition = true;
            }

            if (viewportPoint.y < 0 || viewportPoint.y > 1)
            {
                float moveAmountY = GetCameraHalfHeightAtTarget() * camMoveMod;
                if (viewportPoint.y < 0)
                {
                    targetPosition -= new Vector3(0, moveAmountY, 0);
                }
                else if (viewportPoint.y > 1)
                {
                    targetPosition += new Vector3(0, moveAmountY, 0);
                }
            }

            transform.position = Vector3.Lerp(transform.position, targetPosition, Time.deltaTime * transitionSpeed);
        }

        disableTransition = false;
    }
}
