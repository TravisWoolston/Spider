using UnityEngine;
using UnityEngine.Rendering.Universal;
using Clipper2Lib;
using System.Collections.Generic;

/// <summary>
/// This component now acts as a Chunk Manager.
/// It creates and manages a grid of DestructibleTerrainChunk children to localized 
/// physics and mesh updates, preventing big frame freezes.
/// </summary>
public class DestructibleTerrain : MonoBehaviour
{
    [Header("Chunking Settings")]
    public float chunkSize = 10f;
    public GameObject chunkPrefab; // Optional, will auto-create if null

    [Header("Visuals")]
    public Color terrainColor = new Color(0.35f, 0.22f, 0.13f, 1f);
    public Material customMaterial;

    [Header("Layer Sorting")]
    public string sortingLayerName = "Default";
    public int sortingOrder = 0;

    [Header("Shadow Caster (Surface Only)")]
    [Tooltip("Uses the 'Infinite Top' shadow logic - very cheap, clean shadows from the surface only.")]
    public bool enableSurfaceShadowProxy = true;

    private Dictionary<Vector2Int, DestructibleTerrainChunk> chunks = new Dictionary<Vector2Int, DestructibleTerrainChunk>();
    
    // For the surface shadow proxy
    private GameObject shadowProxy;
    private PolygonCollider2D shadowCollider;
    private ShadowCaster2D shadowCaster;
    private Vector2[] surfaceProfile;

    void Awake()
    {
        // The chunks dictionary is not serialized, so rebuild it from any
        // chunk children that were persisted in the scene from editor generation.
        RebuildChunkDictionary();
    }

    /// <summary>
    /// Scan child GameObjects for DestructibleTerrainChunk components and
    /// rebuild the lookup dictionary from their names (Chunk_X_Y).
    /// </summary>
    private void RebuildChunkDictionary()
    {
        chunks.Clear();
        for (int i = 0; i < transform.childCount; i++)
        {
            var child = transform.GetChild(i);
            var chunk = child.GetComponent<DestructibleTerrainChunk>();
            if (chunk == null) continue;

            // Parse the grid key from the name: "Chunk_X_Y"
            string[] parts = child.name.Split('_');
            if (parts.Length >= 3
                && int.TryParse(parts[1], out int x)
                && int.TryParse(parts[2], out int y))
            {
                chunks[new Vector2Int(x, y)] = chunk;
            }
        }

        if (chunks.Count > 0)
            Debug.Log($"[DestructibleTerrain] Rebuilt dictionary: {chunks.Count} chunks found from scene hierarchy.");
    }

    /// <summary>
    /// Split the generated terrain into chunks.
    /// </summary>
    public void SetGeneratorResults(Vector2[] profile, float height, PathsD paths)
    {
        surfaceProfile = profile;
        ClearChunks();

        // Find total bounds to determine grid size
        RectD bounds = Clipper.GetBounds(paths);
        int minX = Mathf.FloorToInt((float)bounds.left / chunkSize);
        int maxX = Mathf.CeilToInt((float)bounds.right / chunkSize);
        int minY = Mathf.FloorToInt((float)bounds.top / chunkSize);
        int maxY = Mathf.CeilToInt((float)bounds.bottom / chunkSize);

        Debug.Log($"[DestructibleTerrain] Splitting {paths.Count} paths into chunks. Bounds: {bounds.left},{bounds.bottom} to {bounds.right},{bounds.top}");
        int chunkCount = 0;

        for (int x = minX; x < maxX; x++)
        {
            for (int y = minY; y < maxY; y++)
            {
                // Create a slightly expanded rectangle for intersecting to prevent seams
                float bleed = 0.05f; 
                PathD chunkRectPath = new PathD(4);
                chunkRectPath.Add(new PointD(x * chunkSize - bleed, y * chunkSize - bleed));
                chunkRectPath.Add(new PointD((x + 1) * chunkSize + bleed, y * chunkSize - bleed));
                chunkRectPath.Add(new PointD((x + 1) * chunkSize + bleed, (y + 1) * chunkSize + bleed));
                chunkRectPath.Add(new PointD(x * chunkSize - bleed, (y + 1) * chunkSize + bleed));

                // Use Intersect (standard boolean op) which is more reliable for complex holes than RectClip
                PathsD chunkPaths = Clipper.Intersect(paths, new PathsD { chunkRectPath }, FillRule.NonZero, 2);
                
                if (chunkPaths.Count > 0)
                {
                    DestructibleTerrainChunk chunk = GetOrCreateChunk(x, y);
                    chunk.Initialize(chunkPaths, terrainColor, customMaterial, sortingLayerName, sortingOrder);
                    chunkCount++;
                }
            }
        }

        Debug.Log($"[DestructibleTerrain] Total Paths: {paths.Count}. Master Bounds: {bounds.left},{bounds.top} to {bounds.right},{bounds.bottom}. Generated {chunkCount} chunks.");

        // Disable the parent's own PolygonCollider2D (if any) — chunks now own collision.
        // The old RequireComponent on ProceduralTerrainGenerator creates one, but it's stale
        // and would intercept raycasts meant for chunk colliders.
        var parentCol = GetComponent<PolygonCollider2D>();
        if (parentCol != null) parentCol.enabled = false;

        if (enableSurfaceShadowProxy) UpdateShadowCaster();
    }

    private DestructibleTerrainChunk GetOrCreateChunk(int x, int y)
    {
        Vector2Int key = new Vector2Int(x, y);
        if (chunks.TryGetValue(key, out var chunk)) return chunk;

        GameObject go = new GameObject($"Chunk_{x}_{y}");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.layer = gameObject.layer;

        chunk = go.AddComponent<DestructibleTerrainChunk>();
        chunks[key] = chunk;
        return chunk;
    }

    private void ClearChunks()
    {
        // Destroy ALL chunk children — not just dictionary-tracked ones.
        // Editor-time generation serializes chunks into the scene, but at Play mode
        // the dictionary is fresh/empty, so we'd miss those stale GameObjects.
        var toDestroy = new List<GameObject>();
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var child = transform.GetChild(i);
            // Destroy anything that's a chunk or has a broken script ref from before
            if (child.GetComponent<DestructibleTerrainChunk>() != null
                || child.name.StartsWith("Chunk_"))
            {
                toDestroy.Add(child.gameObject);
            }
        }
        foreach (var go in toDestroy)
        {
            if (Application.isPlaying) Destroy(go);
            else DestroyImmediate(go);
        }
        chunks.Clear();
    }

    public void MineHole(Vector2 worldPos, float radius, int circleSegments = 16)
    {
        Vector2 localPos = transform.InverseTransformPoint(worldPos);
        
        // Find chunks in range
        int startX = Mathf.FloorToInt((localPos.x - radius) / chunkSize);
        int endX = Mathf.FloorToInt((localPos.x + radius) / chunkSize);
        int startY = Mathf.FloorToInt((localPos.y - radius) / chunkSize);
        int endY = Mathf.FloorToInt((localPos.y + radius) / chunkSize);

        int hitCount = 0;
        for (int x = startX; x <= endX; x++)
        {
            for (int y = startY; y <= endY; y++)
            {
                if (chunks.TryGetValue(new Vector2Int(x, y), out var chunk))
                {
                    chunk.MineHole(localPos, radius, circleSegments);
                    hitCount++;
                }
            }
        }

        if (hitCount == 0)
        {
            Debug.LogWarning($"[DestructibleTerrain] Dig at local {localPos} (world {worldPos}) missed all chunks. Grid range: ({startX},{startY})-({endX},{endY}). Tracked chunks: {chunks.Count}");
        }
    }

    public void EnsureVisuals() { /* Handled by chunks */ }
    public void UpdateMesh() { /* Handled by chunks */ }

    #region Surface Shadow Proxy

    public void UpdateShadowCaster()
    {
        if (!enableSurfaceShadowProxy || surfaceProfile == null || surfaceProfile.Length < 2)
        {
            DestroyShadowProxy();
            return;
        }

        EnsureShadowProxy();

        float minX = surfaceProfile[0].x;
        float maxX = surfaceProfile[surfaceProfile.Length - 1].x;

        float lowestY = surfaceProfile[0].y;
        for (int i = 1; i < surfaceProfile.Length; i++)
            if (surfaceProfile[i].y < lowestY) lowestY = surfaceProfile[i].y;
        
        float bottomY = lowestY - 1f;

        Vector2[] shape = new Vector2[surfaceProfile.Length + 2];
        shape[0] = new Vector2(minX, bottomY);
        for (int i = 0; i < surfaceProfile.Length; i++)
            shape[i+1] = surfaceProfile[i];
        shape[surfaceProfile.Length + 1] = new Vector2(maxX, bottomY);

        shadowCollider.pathCount = 1;
        shadowCollider.SetPath(0, shape);
    }

    private void EnsureShadowProxy()
    {
        if (shadowProxy != null) return;
        Transform existing = transform.Find("SurfaceShadowProxy");
        if (existing != null) shadowProxy = existing.gameObject;
        else
        {
            shadowProxy = new GameObject("SurfaceShadowProxy");
            shadowProxy.transform.SetParent(transform, false);
            shadowProxy.transform.localPosition = Vector3.zero;
            shadowProxy.layer = gameObject.layer;
        }

        shadowCollider = shadowProxy.GetComponent<PolygonCollider2D>();
        if (!shadowCollider) shadowCollider = shadowProxy.AddComponent<PolygonCollider2D>();
        shadowCollider.isTrigger = true;

        shadowCaster = shadowProxy.GetComponent<ShadowCaster2D>();
        if (!shadowCaster) shadowCaster = shadowProxy.AddComponent<ShadowCaster2D>();
    }

    private void DestroyShadowProxy()
    {
        if (shadowProxy)
        {
            if (Application.isPlaying) Destroy(shadowProxy);
            else DestroyImmediate(shadowProxy);
        }
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireCube(transform.position + new Vector3(chunkSize * 1f, -chunkSize * 1f, 0), new Vector3(chunkSize * 2f, chunkSize * 2f, 1));
        
        if (chunks != null)
        {
            Gizmos.color = new Color(0, 1, 0, 0.2f);
            foreach (var kvp in chunks)
            {
                Vector3 pos = transform.position + new Vector3(kvp.Key.x * chunkSize + chunkSize * 0.5f, kvp.Key.y * chunkSize + chunkSize * 0.5f, 0);
                Gizmos.DrawWireCube(pos, new Vector3(chunkSize, chunkSize, 0));
            }
        }
    }

    #endregion
}

