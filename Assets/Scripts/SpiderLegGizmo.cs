using UnityEngine;
using System.Collections.Generic;

[ExecuteInEditMode]
public class SpiderLegGizmo : MonoBehaviour
{
    public SpiderController spiderController;

    private float gizmoSize = .1f;
    public SpiderLeg spiderLeg;
    void OnEnable(){
        spiderLeg = gameObject.GetComponent<SpiderLeg>();
    }
    void OnDrawGizmos()
    {



            foreach (AttachmentPoint point in spiderLeg.footAttachmentPoints)
            {

                    Gizmos.DrawSphere(point.point, gizmoSize);
             
            }
        
    }
}



