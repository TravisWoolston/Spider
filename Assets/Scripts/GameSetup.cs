using UnityEngine;

public class GameSetup : MonoBehaviour
{
    
    private float updateTimer = 0f;
    private float updateInterval = 0.1f;

    void FixedUpdate()
    {
        updateTimer += Time.fixedDeltaTime;
        if (updateTimer >= updateInterval)
        {
            GlobalAttachmentManager.UpdateWebAttachmentPoints();
            updateTimer = 0f;
        }
    }
    void Start()
    {
        // Assign unique layers to all spider legs
        // LegLayerManager.AssignUniqueLegLayers();
    }
}
