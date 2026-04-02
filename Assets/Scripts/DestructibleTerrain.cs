using UnityEngine;
using Clipper2Lib;
using System.Collections.Generic;

/// <summary>
/// Chunk Manager for destructible terrain.
/// Creates and manages a grid of DestructibleTerrainChunk children to localize 
/// physics and mesh updates, preventing big frame freezes.
/// Each chunk gets its own ShadowCaster2D for per-chunk shadow casting.
/// </summary>
public class DestructibleTerrain : MonoBehaviour
{
    [Header("Chunking Settings")]
    public float chunkSize = 10f;

    [Header("Visuals")]
    public Color terrainColor = new Color(0.35f, 0.22f, 0.13f, 1f);
    public Material customMaterial;

    [Header("Layer Sorting")]
    public string sortingLayerName = "Default";
    public int sortingOrder = 0;

    [Header("Sun")]
    [Tooltip("Direction the sun/light comes from in world space. If empty, the current transform is used.")]
    public Transform sunTransform;
    [Tooltip("Fallback sun direction in world space when no sunTransform is assigned.")]
    public Vector2 sunDirection = new Vector2(0.5f, -1f);

    [Header("Fog Effects")]
    public bool enableFogEffects = true;

    [Header("Underground Fog")]
    [Tooltip("Maximum distance from an edge before terrain is fully dark")]
    public float fogFadeDistance = 5f;
    [Tooltip("Darkness intensity (0 = invisible, 1 = fully opaque black)")]
    [Range(0f, 1f)]
    public float fogOpacity = 0.95f;

    [Header("Fog Curve")]
    [Tooltip("Higher values make the terrain darken sooner. 1 = linear, 2+ = much more aggressive.")]
    [Range(0.2f, 4f)]
    public float fogAggressiveness = 2f;

    private Dictionary<Vector2Int, DestructibleTerrainChunk> chunks = new Dictionary<Vector2Int, DestructibleTerrainChunk>();

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
    public void SetGeneratorResults(PathsD paths)
    {
        ClearChunks();

        Vector2 sunDir = GetSunDirection();
        Vector2 sunOrigin = transform.InverseTransformPoint(sunTransform != null ? sunTransform.position : transform.position);

        RectD bounds = Clipper.GetBounds(paths);
        Vector2[] corners = new Vector2[]
        {
            new Vector2((float)bounds.left, (float)bounds.top),
            new Vector2((float)bounds.right, (float)bounds.top),
            new Vector2((float)bounds.right, (float)bounds.bottom),
            new Vector2((float)bounds.left, (float)bounds.bottom)
        };
        float minProj = float.MaxValue;
        float maxProj = float.MinValue;
        foreach (var c in corners)
        {
            float p = Vector2.Dot(c, -sunDir);
            if (p < minProj) minProj = p;
            if (p > maxProj) maxProj = p;
        }
        if (Mathf.Approximately(minProj, maxProj)) maxProj = minProj + 0.001f;

        // Find total bounds to determine grid size
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
                    chunk.SetFogEnabled(enableFogEffects);
                    if (enableFogEffects)
                    {
                        chunk.SetFogSettings(fogFadeDistance, fogOpacity);
                        chunk.SetFogAggressiveness(fogAggressiveness);
                        chunk.SetSunDirection(sunDir);
                        chunk.SetSunOrigin(sunOrigin);
                        chunk.SetSunProjectionRange(minProj, maxProj);
                    }
                    chunk.Initialize(chunkPaths, terrainColor, customMaterial, sortingLayerName, sortingOrder);
                    chunkCount++;
                }
            }
        }

        Debug.Log($"[DestructibleTerrain] Generated {chunkCount} chunks.");

        // Disable the parent's own PolygonCollider2D (if any) — chunks now own collision.
        var parentCol = GetComponent<PolygonCollider2D>();
        if (parentCol != null) parentCol.enabled = false;
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
            if (child.GetComponent<DestructibleTerrainChunk>() != null
                || child.name.StartsWith("Chunk_")
                || child.name == "SurfaceShadowProxy")
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

    private Vector2 GetSunDirection()
    {
        Vector2 dir = sunTransform != null ? (Vector2)transform.InverseTransformDirection(sunTransform.right) : sunDirection;
        if (dir.sqrMagnitude < 0.0001f) dir = new Vector2(0.5f, -1f);
        return dir.normalized;
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
}
