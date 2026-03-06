using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CentipedeRaycaster : MonoBehaviour
{
    // Start is called before the first frame update

    private SpiderController spiderController;
    public SpiderLeg spiderLeg;
    public List<AttachmentPoint> points;
    public int pointsCount;
    public float anchorSet = -3.5f;

    // Update is called once per frame
    void Start()
    {
        spiderController = this.transform.parent.gameObject.GetComponent<SpiderController>();

        spiderLeg = gameObject.GetComponent<SpiderLeg>();
    }

    void FixedUpdate()
    {
        PerformRaycasts();
    }

    void Update()
    {
        if (
            spiderLeg.raiseTimer < spiderController.raiseTime
            || (spiderLeg.isCollidingWithDefault && !spiderLeg.isAttached)
        )
        {
            {
                anchorSet =
                    spiderLeg.hingeSegment2.connectedAnchor.x - spiderController.centiLegSliderRate;
                spiderLeg.hingeSegment2.connectedAnchor = new Vector2(anchorSet, 0);
            }
        }
        else if (spiderLeg.isAttached && spiderLeg.raiseTimer > spiderController.raiseTime)
        {
            if (spiderLeg.hingeSegment2.connectedAnchor.x >= spiderController.centiRestingHeight)
            {
                anchorSet =
                    spiderLeg.hingeSegment2.connectedAnchor.x
                    - spiderController.centiLegSliderRate / 3;
                spiderLeg.hingeSegment2.connectedAnchor = new Vector2(anchorSet, 0);
            }
            else
            {
                anchorSet =
                    spiderLeg.hingeSegment2.connectedAnchor.x
                    + spiderController.centiLegSliderRate / 3;
                spiderLeg.hingeSegment2.connectedAnchor = new Vector2(anchorSet, 0);
            }
        }
        else
        {
            if (spiderLeg.hingeSegment2.connectedAnchor.x <= spiderController.centiRaiseMin)
            {
                anchorSet =
                    spiderLeg.hingeSegment2.connectedAnchor.x + spiderController.centiLegSliderRate;
                spiderLeg.hingeSegment2.connectedAnchor = new Vector2(anchorSet, 0);
            }
        }
    }

    public void PerformRaycasts()
    {
        float angleStep = 360f / spiderController.rayCount;

        points = new List<AttachmentPoint>();
        for (int i = 0; i < spiderController.rayCount; i++)
        {
            float angle = i * angleStep;
            Vector2 direction = new Vector2(
                Mathf.Cos(angle * Mathf.Deg2Rad),
                Mathf.Sin(angle * Mathf.Deg2Rad)
            );
            RaycastHit2D hit = Physics2D.Raycast(
                transform.position,
                direction,
                spiderController.legSightDistance,
                spiderController.attachableLayers
            );
            if (hit.collider != null)
            {
                Vector2 hitPoint = hit.point;
                Vector2 hitNormal = hit.normal;
                AttachmentPoint AP = new AttachmentPoint(hitPoint, hitNormal);
                // if (spiderController.attachmentPoints.Contains(AP))
                // {
                //     continue;
                // }
                // if(gameObject.tag != "Centipede")

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
        spiderLeg.attachmentPoints = points;
        pointsCount = points.Count;
        spiderLeg.SetNewTarget();
        // for (int i = 0; i < spiderController.attachmentPoints.Count; i++)
        // {
        //     if (Vector2.Distance(spiderController.attachmentPoints[i], head.position) > spiderController.legSightDistance)
        //     {
        //         spiderController.attachmentPoints.Remove(spiderController.attachmentPoints[i]);
        //         i--;
        //     }
        // }
    }

    void OnDrawGizmos()
    {
        if (spiderController == null)
        {
            spiderController = this.transform.parent.gameObject.GetComponent<SpiderController>();
        }
        if (!spiderController.useCentiBodyGizmo)
            return;
        foreach (var point in points)
        {
            Gizmos.DrawLine(point.point, transform.position);
        }
    }
}
