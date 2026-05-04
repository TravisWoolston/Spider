using UnityEngine;
using UnityEngine.Rendering.Universal;
using Clipper2Lib;
using System.Collections.Generic;
using System.Reflection;

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

    [Header("Background Wall Settings")]
    [Tooltip("Generates a solid back wall behind the caves to catch shadows")]
    public bool generateBackgroundWall = true;
    public Color wallColor = new Color(0.2f, 0.15f, 0.1f, 1f);
    [Tooltip("How far back to place the wall (Z axis)")]
    public float wallZOffset = 1.0f;
    public string wallSortingLayerName = "Default";
    public int wallSortingOrder = -1;

    public enum ShadowOption { None = 0, CastOnly = 1, CastAndSelfShadow = 2 }

    [Header("Shadow Caster Automation")]
    [Tooltip("Inset distance for the shadow shape. Positive values shrink the shadow inward from terrain edges, creating a light-bleed zone at cave openings.")]
    public float shadowInset = 1.38f;
    [Tooltip("Minimum polygon area to keep after digging. Smaller fragments are removed.")]
    public float minFragmentArea = 0.5f;
    [Tooltip("Which layers this wall should cast shadows onto")]
    public string[] shadowTargetLayers = new string[] { "Default", "Terrain_Background" };
    [Tooltip("The casting mode for the background walls")]
    public ShadowOption shadowCastingOption = ShadowOption.CastAndSelfShadow;

    [Header("Layer Sorting")]
    public string foregroundPhysicsLayer = "Default";
    public uint foregroundRenderingLayerMask = 1;
    [Space]
    public string wallPhysicsLayer = "Default";
    public uint wallRenderingLayerMask = 1;
    [Space]
    public string sortingLayerName = "Default";
    public int sortingOrder = 0;

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

        RectD bounds = Clipper.GetBounds(paths);

        // Find total bounds to determine grid size
        int minX = Mathf.FloorToInt((float)bounds.left / chunkSize);
        int maxX = Mathf.CeilToInt((float)bounds.right / chunkSize);
        int minY = Mathf.FloorToInt((float)bounds.top / chunkSize);
        int maxY = Mathf.CeilToInt((float)bounds.bottom / chunkSize);

        Debug.Log($"[DestructibleTerrain] Splitting {paths.Count} paths into chunks. Bounds: {bounds.left},{bounds.bottom} to {bounds.right},{bounds.top}");
        int chunkCount = 0;

        // Compute the shadow inset on the FULL terrain before chunking so that
        // chunk boundary edges are not shrunk inward (which would create gaps).
        PathsD globalInsetPaths = paths;
        if (shadowInset > 0f)
        {
            PathsD inset = Clipper.InflatePaths(paths, -shadowInset,
                Clipper2Lib.JoinType.Round, Clipper2Lib.EndType.Polygon, 2, 2);
            if (inset != null && inset.Count > 0)
                globalInsetPaths = inset;
        }

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

                // Foreground: The terrain with caves
                PathsD chunkPaths = Clipper.Intersect(paths, new PathsD { chunkRectPath }, FillRule.NonZero, 2);

                if (chunkPaths.Count > 0)
                {
                    DestructibleTerrainChunk chunk = GetOrCreateChunk(x, y);
                    chunk.SetSkipFlags(noShadowCaster: true, noCollider: false);

                    if (chunk != null)
                    {
                        chunk.Initialize(chunkPaths, terrainColor, customMaterial, sortingLayerName, sortingOrder);

                        // Apply Foreground Rendering Layer
                        var chunkRenderer = chunk.GetComponent<MeshRenderer>();
                        if (chunkRenderer != null) chunkRenderer.renderingLayerMask = foregroundRenderingLayerMask;

                        // Optional: Background 'Back Wall'
                        if (generateBackgroundWall)
                        {
                            string wallName = $"Wall_{x}_{y}";
                            Transform wallTrans = chunk.transform.Find(wallName);
                            if (!wallTrans)
                            {
                                GameObject wallGO = new GameObject(wallName);
                                wallGO.transform.SetParent(chunk.transform, false);
                                wallGO.layer = LayerMask.NameToLayer(wallPhysicsLayer);
                                wallTrans = wallGO.transform;
                            }
                            // Keep the Wall Game Object exactly at Z=0 so its ShadowCaster2D doesn't slide with Camera Parallax
                            wallTrans.localPosition = Vector3.zero;

                            var wallChunk = wallTrans.GetComponent<DestructibleTerrainChunk>();
                            if (!wallChunk) wallChunk = wallTrans.gameObject.AddComponent<DestructibleTerrainChunk>();
                            
                            // Let the chunk know to manually push its mesh vertices back into the distance for the visual parallax effect
                            wallChunk.SetMeshZOffset(wallZOffset);
                            
                            wallChunk.SetSkipFlags(noShadowCaster: false, noCollider: true);
                            // shadowInset=0: wall receives pre-inset paths, no per-chunk re-inset.
                            wallChunk.SetShadowInset(0f);

                            // Pre-configure the shadow caster BEFORE Initialize so that
                            // SyncShadowCaster (called inside Initialize) writes our inset
                            // shape into an already-configured caster. This prevents URP's
                            // Awake/Update from overwriting our shape with renderer bounds.
                            var wallSC = wallTrans.GetComponent<ShadowCaster2D>();
                            if (!wallSC) wallSC = wallTrans.gameObject.AddComponent<ShadowCaster2D>();
                            wallSC.enabled = true;
                            ConfigureShadowCaster(wallSC);

                            // Clip the globally-inset paths to this chunk rect so wall
                            // mesh + shadow use the seamless pre-inset shape.
                            PathsD wallChunkPaths = Clipper.Intersect(globalInsetPaths, new PathsD { chunkRectPath }, FillRule.NonZero, 2);
                            if (wallChunkPaths == null || wallChunkPaths.Count == 0)
                                wallChunkPaths = chunkPaths;

                            wallChunk.Initialize(wallChunkPaths, wallColor, customMaterial, wallSortingLayerName, wallSortingOrder);

                            // Apply Rendering Layer
                            var wallRenderer = wallChunk.GetComponent<MeshRenderer>();
                            if (wallRenderer != null)
                                wallRenderer.renderingLayerMask = wallRenderingLayerMask;
                        }
                    }

                    chunkCount++;
                }
            }
        }

        Debug.Log($"[DestructibleTerrain] Generated {chunkCount} chunks.");

        // Disable the parent's own PolygonCollider2D (if any) — chunks now own collision.
        var parentCol = GetComponent<PolygonCollider2D>();
        if (parentCol != null) parentCol.enabled = false;

        // Notify the LOD manager that chunks have been rebuilt.
        var lodManager = GetComponent<ChunkLODManager>();
        if (lodManager != null) lodManager.OnTerrainRegenerated();
    }

    /// <summary>
    /// Configure a ShadowCaster2D using exact URP field names via reflection.
    /// Sets casting source to ShapeEditor, casting option, target sorting layers,
    /// and disables renderer silhouette. Shadow inset is applied via Clipper in
    /// DestructibleTerrainChunk.SyncShadowCaster rather than URP's trimEdge
    /// (which is non-functional in ShapeEditor mode).
    /// </summary>
    private void ConfigureShadowCaster(ShadowCaster2D sc)
    {
        var type = typeof(ShadowCaster2D);
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        try
        {
            // Force ShapeEditor mode so URP uses m_ShapePath data
            // ShadowCastingSources: None=0, ShapeEditor=1, ShapeProvider=2
            var sourceField = type.GetField("m_ShadowCastingSource", flags);
            if (sourceField != null)
                sourceField.SetValue(sc, System.Enum.ToObject(sourceField.FieldType, 1)); // ShapeEditor = 1

            // Disable renderer silhouette (use our polygon shape, not the sprite bounds)
            var silhouetteField = type.GetField("m_UseRendererSilhouette", flags);
            if (silhouetteField != null)
                silhouetteField.SetValue(sc, false);

            // Apply target sorting layers
            var layerField = type.GetField("m_ApplyToSortingLayers", flags);
            if (layerField != null && shadowTargetLayers != null)
            {
                int[] layerIDs = new int[shadowTargetLayers.Length];
                for (int i = 0; i < shadowTargetLayers.Length; i++)
                    layerIDs[i] = SortingLayer.NameToID(shadowTargetLayers[i]);
                layerField.SetValue(sc, layerIDs);
            }

            // Apply casting option (SelfShadow=0, CastShadow=1, CastAndSelfShadow=2, NoShadow=3)
            var castOptionField = type.GetField("m_CastingOption", flags);
            if (castOptionField != null)
            {
                // Map our ShadowOption enum to URP's ShadowCastingOptions
                int urpValue;
                switch (shadowCastingOption)
                {
                    case ShadowOption.None: urpValue = 3; break; // NoShadow
                    case ShadowOption.CastOnly: urpValue = 1; break; // CastShadow
                    case ShadowOption.CastAndSelfShadow: urpValue = 2; break; // CastAndSelfShadow
                    default: urpValue = 1; break;
                }
                castOptionField.SetValue(sc, System.Enum.ToObject(castOptionField.FieldType, urpValue));
            }

            // Note: URP's ShapeEditor mode hardcodes allowTrimming=false in
            // SetShadowShape -> SetShapeWithLines, so trimEdge is impossible via
            // reflection. Instead, shadow inset is applied via Clipper.InflatePaths
            // in DestructibleTerrainChunk.SyncShadowCaster before writing m_ShapePath.

            // Force URP to rebuild shadow mesh after our configuration changes.
            var forceField = type.GetField("m_ForceShadowMeshRebuild", flags);
            if (forceField != null)
                forceField.SetValue(sc, true);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[DestructibleTerrain] Shadow caster configuration failed: {e.Message}");
        }
    }

    private DestructibleTerrainChunk GetOrCreateChunk(int x, int y)
    {
        Vector2Int key = new Vector2Int(x, y);
        if (chunks.TryGetValue(key, out var chunk)) return chunk;

        GameObject go = new GameObject($"Chunk_{x}_{y}");
        go.transform.SetParent(transform, false);
        go.layer = LayerMask.NameToLayer(foregroundPhysicsLayer);

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

        // Collect affected chunks and dig the foreground.
        var affectedChunks = new List<(Vector2Int key, DestructibleTerrainChunk chunk)>();
        for (int x = startX; x <= endX; x++)
        {
            for (int y = startY; y <= endY; y++)
            {
                var key = new Vector2Int(x, y);
                if (chunks.TryGetValue(key, out var chunk))
                {
                    // Force-activate chunks that the LOD manager may have disabled
                    if (!chunk.gameObject.activeSelf)
                        chunk.gameObject.SetActive(true);

                    chunk.MineHole(localPos, radius, circleSegments, minFragmentArea);
                    affectedChunks.Add((key, chunk));
                }
            }
        }

        if (affectedChunks.Count == 0)
        {
            Debug.LogWarning($"[DestructibleTerrain] Dig at local {localPos} (world {worldPos}) missed all chunks. Grid range: ({startX},{startY})-({endX},{endY}). Tracked chunks: {chunks.Count}");
            return;
        }

        // Compute wall shadow inset across all affected chunks at once so
        // chunk boundary edges stay flush (no visible seams between shadows).
        if (shadowInset > 0f && generateBackgroundWall)
        {
            // Merge the post-dig foreground paths from all affected chunks.
            PathsD merged = new PathsD();
            foreach (var (key, chunk) in affectedChunks)
            {
                PathsD chunkPaths = chunk.GetPaths();
                if (chunkPaths != null)
                    merged.AddRange(chunkPaths);
            }

            // Compute the inset on the merged region.
            PathsD mergedInset = merged;
            if (merged.Count > 0)
            {
                PathsD insetResult = Clipper.InflatePaths(merged, -shadowInset,
                    Clipper2Lib.JoinType.Round, Clipper2Lib.EndType.Polygon, 2, 2);
                if (insetResult != null && insetResult.Count > 0)
                    mergedInset = insetResult;
            }

            // Clip the merged inset back to each chunk's rect and push to walls.
            float bleed = 0.05f;
            foreach (var (key, chunk) in affectedChunks)
            {
                PathD chunkRect = new PathD(4);
                chunkRect.Add(new PointD(key.x * chunkSize - bleed, key.y * chunkSize - bleed));
                chunkRect.Add(new PointD((key.x + 1) * chunkSize + bleed, key.y * chunkSize - bleed));
                chunkRect.Add(new PointD((key.x + 1) * chunkSize + bleed, (key.y + 1) * chunkSize + bleed));
                chunkRect.Add(new PointD(key.x * chunkSize - bleed, (key.y + 1) * chunkSize + bleed));

                PathsD wallPaths = Clipper.Intersect(mergedInset, new PathsD { chunkRect }, FillRule.NonZero, 2);
                if (wallPaths == null || wallPaths.Count == 0)
                    wallPaths = chunk.GetPaths();

                chunk.SetWallPaths(wallPaths);
            }
        }

        // Notify the LOD manager to invalidate skylight cache for affected chunks
        var lodManager = GetComponent<ChunkLODManager>();
        if (lodManager != null) lodManager.OnChunksDug(worldPos, radius);
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
