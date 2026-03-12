using UnityEngine;
using System.Collections;
public class GridCameraController : MonoBehaviour
{
    public Transform spiderHead; // Reference to the spider's head
    public Vector3 offset; // Offset from the spider's head

    private Camera cam;
    private Vector3 initialPosition;
    public float camMoveMod = 2f;
    public float transitionMod = 0f;
    public float transitionModStore;
    public bool disableTransition;
     public Vector3 targetPosition; // Target position for the camera
    public float transitionSpeed = 2f;
    public float camZoom = .1f;
    public bool followSpider = false;
    public float followSmoothing = 5f;

    void Start()
    {
        cam = GetComponentInChildren<Camera>();

        if (spiderHead == null)
        {
            Debug.LogError("Spider head not assigned to CameraController.");
            enabled = false;
            return;
        }
        initialPosition = transform.position;
        transitionModStore = transitionMod;
        targetPosition = transform.position;
    }

    void LateUpdate()
    {
        if (Input.GetKey(KeyCode.Keypad1))
        {
            cam.orthographicSize -= camZoom;
        }
        if (Input.GetKey(KeyCode.Keypad2))
        {
            cam.orthographicSize += camZoom;
        }

        if (spiderHead == null || cam == null) return;

        if (followSpider)
        {
            // Smoothly follow the spider's head directly
            Vector3 goal = new Vector3(spiderHead.position.x, spiderHead.position.y, transform.position.z) + offset;
            transform.position = Vector3.Lerp(transform.position, goal, Time.deltaTime * followSmoothing);
            targetPosition = transform.position; // Keep targetPosition synced for when toggle is turned off
        }
        else
        {
            // Existing Grid-based movement logic
            Vector3 viewportPoint = cam.WorldToViewportPoint(spiderHead.position);
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
                targetPosition.y = spiderHead.position.y;
            }

            if ((viewportPoint.x < 0 + transitionMod || viewportPoint.x > 1 - transitionMod) && !disableTransition)
            {
                float moveAmountX = cam.orthographicSize * cam.aspect * (camMoveMod - transitionMod);
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
                float moveAmountY = cam.orthographicSize * camMoveMod;
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
