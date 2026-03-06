using UnityEngine;
using System.Collections;

public class SpiderWebShooter : MonoBehaviour
{
    public GameObject webPrefab;
    public Transform webStartPoint;
    public int maxWebSegments = 10;
    public LayerMask defaultLayer;
    public float segmentLength = 1f;
    public float overlapRadius = 0.2f; // Radius for checking overlaps
    public int stringId = 0;
    public BoxCollider2D webRb;
    void Start(){
        // overlapRadius = segmentLength/2;
        segmentLength = webRb.bounds.extents.x;
    }
    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Mouse1))
        {
            StartCoroutine(ShootWeb());
        }
    }

    IEnumerator ShootWeb()
    {
        Vector2 startPoint = webStartPoint.position;
        Vector2 targetPosition = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        Vector2 direction = (targetPosition - (Vector2)webStartPoint.position).normalized;

        GameObject webString = new GameObject("WebString");
        int currentSegmentCount = 0;
        GameObject previousSegment = null;

        for (int i = 0; i < maxWebSegments; i++)
        {
            GameObject webSegmentGO = Instantiate(webPrefab, startPoint, Quaternion.identity, webString.transform);

            WebSegment webSegment = webSegmentGO.GetComponent<WebSegment>();
            webSegment.overlapRadius = overlapRadius;
            webSegment.defaultLayer = defaultLayer;
            webSegment.stringId = stringId;
            webSegment.segmentLength = segmentLength;
            if(i == 0){
                webSegment.firstSegment = true;
                webSegment.hinge2.enabled = false;
            }
            float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
            webSegmentGO.transform.rotation = Quaternion.Euler(0, 0, angle);

            if (previousSegment != null)
            {
                WebSegment previousWebSegment = previousSegment.GetComponent<WebSegment>();
                previousWebSegment.hinge.connectedBody = webSegmentGO.GetComponent<Rigidbody2D>();
                webSegment.hinge2.connectedBody = previousWebSegment.gameObject.GetComponent<Rigidbody2D>();
            }

            previousSegment = webSegmentGO;
            startPoint += direction * segmentLength;
            currentSegmentCount++;

            // Check for overlap with default surfaces or other web strings
            if (webSegment.CheckForSurfaceCollision(direction))
            {
                break;
            }

            yield return new WaitForSeconds(0f);
        }
        stringId++;
    }
}

