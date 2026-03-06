using UnityEngine;

public class GameSetup : MonoBehaviour
{
    
void FixedUpdate()
{
    GlobalAttachmentManager.UpdateWebAttachmentPoints();
    // GlobalAttachmentManager.RemoveStalePoints();
}
    void Start()
    {
        // Assign unique layers to all spider legs
        // LegLayerManager.AssignUniqueLegLayers();
    }
}
