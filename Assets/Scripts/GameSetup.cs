using UnityEngine;

public class GameSetup : MonoBehaviour
{
    public static GameSetup Instance;
    
    [Header("AI Debug")]
    public bool showAIDebug = true;
    
    [Header("Terrain Caching")]
    public float terrainCacheDensity = 1.5f;

    private float updateTimer = 0f;
    private float updateInterval = 0.1f;

    void Awake()
    {
        Instance = this;
    }

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
        // Auto-cache if we haven't already
        if (GlobalAttachmentManager.TotalPoints == 0)
        {
            GlobalAttachmentManager.minimumDistanceBetweenPoints = terrainCacheDensity;
            CacheTerrain();
        }
    }

    [ContextMenu("Cache Scene Terrain")]
    public void CacheTerrain()
    {
        LayerMask mask = LayerMask.GetMask("Default");
        GlobalAttachmentManager.minimumDistanceBetweenPoints = terrainCacheDensity;
        GlobalAttachmentManager.CacheSceneTerrain(mask, terrainCacheDensity);
    }

    [ContextMenu("Clear Global Points")]
    public void ClearPoints()
    {
        GlobalAttachmentManager.ClearGlobalPoints();
    }
}
