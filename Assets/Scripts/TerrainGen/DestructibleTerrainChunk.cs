using UnityEngine;
using UnityEngine.Rendering.Universal;
using Clipper2Lib;
using System.Reflection;

/// <summary>
/// A localized piece of terrain. Handles its own Clipper ops, physics rebaking, 
/// and triangulation to keep updates extremely fast.
/// Must be in its own file (Unity requires filename == MonoBehaviour class name).
/// </summary>
public class DestructibleTerrainChunk : MonoBehaviour
{
    private PolygonCollider2D col;
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private PathsD myPaths;
    private Color terrainColor;
    private bool dirty = false;
    private Mesh cachedMesh;

    // Cache reflection lookups to save CPU
    private static FieldInfo shapePathField;
    private static FieldInfo shadowCastingSourceField;
    private static FieldInfo shapePathHashField;
    private static FieldInfo forceShadowRebuildField;
    private static bool reflectionInitialized = false;

    // Flags to skip unnecessary work on specific chunk types
    private bool skipShadowCaster = false;
    private bool skipCollider = false;
    private bool suppressShadowVisibility = false;

    /// <summary>
    /// When true, UpdateShadowCasterVisibility will not force-enable the shadow caster.
    /// Set by ChunkLODManager when it owns shadow caster enabled/disabled state.
    /// </summary>
    public void SetSuppressShadowVisibility(bool suppress) { suppressShadowVisibility = suppress; }

    // Cached to avoid per-frame string comparison and GetComponent calls
    private bool isWallChunk = false;
    private bool isWallChunkResolved = false;
    private ShadowCaster2D cachedShadowCaster;

    private bool IsWallChunk
    {
        get
        {
            if (!isWallChunkResolved)
            {
                isWallChunk = skipCollider || name.StartsWith("Wall_");
                isWallChunkResolved = true;
            }
            return isWallChunk;
        }
    }

    // Shadow caster debounce: batch rapid digs into fewer shadow rebuilds
    private bool shadowDirty = false;
    private float shadowCooldown = 0.15f;
    private float shadowTimer = 0f;

    // Clipper-based shadow inset (replaces URP trimEdge which doesn't work in ShapeEditor mode)
    private float shadowInset = 0f;
    private PathsD cachedInsetPaths = null;

    // Shadow path simplification: aggressively reduce vertex count on shadow polygons
    // since shadow shapes don't need collision-level detail.
    private static float shadowSimplifyTolerance = 0.5f;
    private static int maxShadowVertices = 32;

    // Camera frustum culling for wall shadow casters
    private static Camera cachedCamera;
    private static int cachedCameraFrame = -1;
    private static Plane[] cachedFrustumPlanes = new Plane[6];
    private Bounds chunkWorldBounds;
    private bool boundsInitialized = false;

    // Hysteresis margin for frustum culling to prevent flickering at edges
    private const float CullingHysteresisMargin = 2f;
    private bool wasInView = true;

    // Depth offset explicitly applied to rendered mesh vertices (prevents ShadowCaster2D from drifting)
    private float meshZOffset = 0f;

    /// <summary>
    /// Configure this chunk to skip shadow caster creation (e.g. foreground chunks
    /// that never cast shadows). Also optionally skip collider for visual-only chunks.
    /// Call before Initialize.
    /// </summary>
    public void SetSkipFlags(bool noShadowCaster, bool noCollider)
    {
        skipShadowCaster = noShadowCaster;
        skipCollider = noCollider;
        isWallChunk = noCollider;
        isWallChunkResolved = true;
    }

    /// <summary>
    /// Explicitly push the generated mesh vertices into the background for perspective parallax,
    /// without tearing the shadow caster out from the Z=0 plane.
    /// </summary>
    public void SetMeshZOffset(float zOffset)
    {
        meshZOffset = zOffset;
    }

    /// <summary>
    /// Set the inset distance for the shadow shape. Positive values shrink the
    /// shadow polygon inward, creating a light-bleed zone at cave edges.
    /// Call before Initialize.
    /// </summary>
    public void SetShadowInset(float inset)
    {
        shadowInset = Mathf.Max(0f, inset);
    }

    public void Initialize(PathsD paths, Color color, Material mat, string layer, int order)
    {
        myPaths = paths;
        cachedInsetPaths = null;
        terrainColor = color;

        if (!skipCollider)
        {
            col = gameObject.GetComponent<PolygonCollider2D>();
            if (!col) col = gameObject.AddComponent<PolygonCollider2D>();
        }
        else
        {
            // Remove any existing collider on visual-only chunks
            var existingCol = gameObject.GetComponent<PolygonCollider2D>();
            if (existingCol) { if (Application.isPlaying) Destroy(existingCol); else DestroyImmediate(existingCol); }
            col = null;
        }

        if (!skipCollider)
        {
            meshFilter = gameObject.GetComponent<MeshFilter>();
            if (!meshFilter) meshFilter = gameObject.AddComponent<MeshFilter>();

            meshRenderer = gameObject.GetComponent<MeshRenderer>();
            if (!meshRenderer) meshRenderer = gameObject.AddComponent<MeshRenderer>();

            if (mat != null)
                meshRenderer.sharedMaterial = mat;
            else if (meshRenderer.sharedMaterial == null)
                meshRenderer.sharedMaterial = new Material(Shader.Find("Universal Render Pipeline/2D/Sprite-Lit-Default"));

            meshRenderer.sortingLayerName = layer;
            meshRenderer.sortingOrder = order;
        }
        else
        {
            // Wall chunks don't need visible rendering — remove any existing
            // MeshRenderer/MeshFilter so nothing can re-enable them.
            var existingRenderer = gameObject.GetComponent<MeshRenderer>();
            if (existingRenderer) { if (Application.isPlaying) Destroy(existingRenderer); else DestroyImmediate(existingRenderer); }
            var existingFilter = gameObject.GetComponent<MeshFilter>();
            if (existingFilter) { if (Application.isPlaying) Destroy(existingFilter); else DestroyImmediate(existingFilter); }
            meshFilter = null;
            meshRenderer = null;
        }

        if (!skipCollider) SyncCollider();
        if (!skipCollider) UpdateMesh();

        // Compute frustum culling bounds from path data for wall chunks.
        if (skipCollider)
            chunkWorldBounds = ComputeWorldBoundsFromPaths();

        // Only add shadow caster when needed (walls cast shadows, foreground doesn't)
        if (!skipShadowCaster)
        {
            cachedShadowCaster = gameObject.GetComponent<ShadowCaster2D>();
            if (!cachedShadowCaster) cachedShadowCaster = gameObject.AddComponent<ShadowCaster2D>();
            SyncShadowCaster();
        }
    }

    /// <summary>
    /// Lazily ensure myPaths and component refs are valid.
    /// Handles the case where the chunk was serialized in the scene but
    /// myPaths (non-serialized) is null — rebuild from the PolygonCollider2D.
    /// </summary>
    private void EnsureReady()
    {
        if (!skipCollider && col == null) col = GetComponent<PolygonCollider2D>();
        if (meshFilter == null) meshFilter = GetComponent<MeshFilter>();
        if (meshRenderer == null) meshRenderer = GetComponent<MeshRenderer>();

        // Recover non-serialized flags for wall chunks loaded from scene.
        if (!skipCollider && name.StartsWith("Wall_"))
        {
            skipCollider = true;
            skipShadowCaster = false;
        }

        // Recover terrain color from the parent manager (it's serialized there)
        if (terrainColor.a < 0.01f)
        {
            var terrain = GetComponentInParent<DestructibleTerrain>();
            if (terrain != null)
                terrainColor = terrain.terrainColor;
            else
                terrainColor = new Color(0.35f, 0.22f, 0.13f, 1f);
        }

        // Rebuild paths from collider if available (foreground chunks only).
        // Wall chunks (skipCollider=true) have no collider — their myPaths
        // are set by Initialize and kept in sync by MineHole propagation.
        if (myPaths == null && col != null && col.pathCount > 0)
        {
            myPaths = new PathsD();
            for (int i = 0; i < col.pathCount; i++)
            {
                Vector2[] pts = col.GetPath(i);
                PathD path = new PathD(pts.Length);
                for (int j = 0; j < pts.Length; j++)
                    path.Add(new PointD(pts[j].x, pts[j].y));
                myPaths.Add(path);
            }
            Debug.Log($"[Chunk {name}] Rebuilt {myPaths.Count} paths from collider.");
        }

        // If this is a wall chunk loaded from scene serialization (no collider,
        // non-serialized myPaths lost), copy the parent's current paths.
        if (myPaths == null && skipCollider)
        {
            var parentChunk = transform.parent != null ? transform.parent.GetComponent<DestructibleTerrainChunk>() : null;
            if (parentChunk != null && parentChunk.myPaths != null)
            {
                myPaths = new PathsD(parentChunk.myPaths.Count);
                foreach (var p in parentChunk.myPaths)
                {
                    PathD copy = new PathD(p.Count);
                    foreach (var pt in p) copy.Add(new PointD(pt.x, pt.y));
                    myPaths.Add(copy);
                }
                cachedInsetPaths = null;
            }
        }

        // Wall chunks are non-serialized at runtime and can lose this value.
        // Recover the configured inset from the parent terrain manager.
        // Use IsWallChunk because skipCollider is not serialized and resets on scene load.
        if (IsWallChunk && shadowInset <= 0f)
        {
            var terrain = GetComponentInParent<DestructibleTerrain>();
            if (terrain != null)
                shadowInset = Mathf.Max(0f, terrain.shadowInset);
        }

        // Seed frustum culling bounds for serialized chunks.
        if (!boundsInitialized)
        {
            if (IsWallChunk && myPaths != null && myPaths.Count > 0)
            {
                chunkWorldBounds = ComputeWorldBoundsFromPaths();
            }
            else if (meshRenderer != null && meshRenderer.bounds.size != Vector3.zero)
            {
                chunkWorldBounds = meshRenderer.bounds;
                boundsInitialized = true;
            }
        }
    }

    public void MineHole(Vector2 localPos, float radius, int circleSegments, float minFragmentArea = 0.5f)
    {
        EnsureReady();
        if (myPaths == null || myPaths.Count == 0) return;

        // Create the circular hole path
        PathD clipPath = new PathD(circleSegments);
        for (int i = 0; i < circleSegments; i++)
        {
            float angle = (i * Mathf.PI * 2f) / circleSegments;
            clipPath.Add(new PointD(
                Mathf.Cos(angle) * (double)radius + (double)localPos.x,
                Mathf.Sin(angle) * (double)radius + (double)localPos.y
            ));
        }

        // Subtract the hole from the terrain using Clipper's Difference
        PathsD result = Clipper.Difference(myPaths, new PathsD { clipPath }, FillRule.NonZero, 2);

        if (result == null) return;

        result = Clipper.SimplifyPaths(result, 0.05, false);

        // Remove tiny remnant fragments that are too small to matter.
        if (minFragmentArea > 0f)
        {
            for (int i = result.Count - 1; i >= 0; i--)
            {
                if (result[i] == null || result[i].Count < 3 ||
                    System.Math.Abs(Clipper.Area(result[i])) < minFragmentArea)
                    result.RemoveAt(i);
            }
        }

        myPaths = result;
        cachedInsetPaths = null;

        // Wall propagation is now handled by DestructibleTerrain.MineHole
        // which computes a unified inset across all affected chunks.

        dirty = true;
    }

    private void SetPathsFromParent(PathsD paths)
    {
        myPaths = paths;
        cachedInsetPaths = null;
        dirty = true;
    }

    /// <summary>
    /// Returns the current foreground paths for this chunk.
    /// Used by DestructibleTerrain to merge paths across chunks for unified inset.
    /// </summary>
    public PathsD GetPaths() => myPaths;

    /// <summary>
    /// Push pre-inset wall paths to this chunk's wall children.
    /// Called by DestructibleTerrain after computing a unified inset across affected chunks.
    /// </summary>
    public void SetWallPaths(PathsD wallPaths)
    {
        foreach (Transform child in transform)
        {
            var childChunk = child.GetComponent<DestructibleTerrainChunk>();
            if (childChunk != null)
                childChunk.SetPathsFromParent(wallPaths);
        }
    }

    private void LateUpdate()
    {
        // One-time init for serialized chunks: seed bounds and recover flags.
        // Do this early and unconditionally to avoid mid-frame bound changes.
        if (!boundsInitialized)
        {
            EnsureReady();
            // Force bounds initialization even if EnsureReady didn't set it
            if (!boundsInitialized && myPaths != null && myPaths.Count > 0)
            {
                chunkWorldBounds = ComputeWorldBoundsFromPaths();
            }
        }

        if (dirty)
        {
            EnsureReady();
            if (!skipCollider) SyncCollider();
            if (!IsWallChunk) UpdateMesh();
            else chunkWorldBounds = ComputeWorldBoundsFromPaths();
            dirty = false;

            // Walls should update shadow immediately when dug.
            if (!skipShadowCaster && IsWallChunk)
            {
                SyncShadowCaster();
                shadowDirty = false;
            }
            else if (!skipShadowCaster)
            {
                shadowDirty = true;
                shadowTimer = shadowCooldown;
            }
        }

        // Only wall chunks need per-frame visibility management.
        if (IsWallChunk && !skipShadowCaster)
        {
            UpdateShadowCasterVisibility();

            // Debounce shadow caster updates — batch rapid digs
            if (shadowDirty)
            {
                shadowTimer -= Time.deltaTime;
                if (shadowTimer <= 0f)
                {
                    SyncShadowCaster();
                    shadowDirty = false;
                }
            }
        }
    }

    private void SyncShadowCaster()
    {
        if (cachedShadowCaster == null) cachedShadowCaster = GetComponent<ShadowCaster2D>();
        var sc = cachedShadowCaster;
        if (sc == null || myPaths == null || myPaths.Count == 0) return;

        if (!reflectionInitialized)
        {
            var type = typeof(ShadowCaster2D);
            shapePathField = type.GetField("m_ShapePath", BindingFlags.NonPublic | BindingFlags.Instance);
            shadowCastingSourceField = type.GetField("m_ShadowCastingSource", BindingFlags.NonPublic | BindingFlags.Instance);
            shapePathHashField = type.GetField("m_ShapePathHash", BindingFlags.NonPublic | BindingFlags.Instance);
            forceShadowRebuildField = type.GetField("m_ForceShadowMeshRebuild", BindingFlags.NonPublic | BindingFlags.Instance);
            reflectionInitialized = true;
        }

        try
        {
            if (shapePathField == null) return;

            // Use inset paths so visible mesh + shadow stay consistent.
            PathsD shadowPaths = GetInsetPaths();
            if (shadowPaths == null || shadowPaths.Count == 0) return;

            // Simplify shadow paths aggressively — shadow shapes don't need
            // the same vertex density as collision or rendering meshes.
            // This is the single biggest perf win for deep caves with complex geometry.
            if (shadowSimplifyTolerance > 0f)
            {
                shadowPaths = Clipper.SimplifyPaths(shadowPaths, shadowSimplifyTolerance, false);
                if (shadowPaths == null || shadowPaths.Count == 0) return;
            }

            // Find the largest valid path without allocating a temp list.
            PathD largestPath = null;
            double largestArea = 0;
            foreach (var p in shadowPaths)
            {
                if (p == null || p.Count < 3) continue;
                double a = System.Math.Abs(Clipper.Area(p));
                if (a > largestArea) { largestArea = a; largestPath = p; }
            }

            if (largestPath == null) return;

            // Further reduce vertex count if still above budget.
            // Use progressively higher tolerance until we're under the cap.
            if (largestPath.Count > maxShadowVertices)
            {
                float tol = shadowSimplifyTolerance * 2f;
                for (int attempt = 0; attempt < 4 && largestPath.Count > maxShadowVertices; attempt++)
                {
                    var simplified = Clipper.SimplifyPaths(
                        new PathsD { largestPath }, tol, false);
                    if (simplified != null && simplified.Count > 0 && simplified[0].Count >= 3)
                        largestPath = simplified[0];
                    tol *= 2f;
                }
            }

            SetShadowPath(sc, largestPath, 0);
        }
        catch { /* Suppress reflection errors on incompatible URP versions */ }
    }

    private void SetShadowPath(ShadowCaster2D caster, PathD path, int index)
    {
        if (caster == null || path == null || path.Count < 3) return;

        if (!reflectionInitialized)
        {
            var type = typeof(ShadowCaster2D);
            shapePathField = type.GetField("m_ShapePath", BindingFlags.NonPublic | BindingFlags.Instance);
            shadowCastingSourceField = type.GetField("m_ShadowCastingSource", BindingFlags.NonPublic | BindingFlags.Instance);
            shapePathHashField = type.GetField("m_ShapePathHash", BindingFlags.NonPublic | BindingFlags.Instance);
            forceShadowRebuildField = type.GetField("m_ForceShadowMeshRebuild", BindingFlags.NonPublic | BindingFlags.Instance);
            reflectionInitialized = true;
        }
        if (shapePathField == null) return;

        if (shadowCastingSourceField != null)
            shadowCastingSourceField.SetValue(caster, System.Enum.ToObject(shadowCastingSourceField.FieldType, 1));

        Vector3[] pts = new Vector3[path.Count];
        int hash = path.Count ^ (index * 73856093);
        for (int i = 0; i < path.Count; i++)
        {
            float px = (float)path[i].x;
            float py = (float)path[i].y;
            pts[i] = new Vector3(px, py, 0f);
            hash = hash * 31 + px.GetHashCode() ^ py.GetHashCode();
        }

        shapePathField.SetValue(caster, pts);
        if (shapePathHashField != null)
            shapePathHashField.SetValue(caster, hash);
        if (forceShadowRebuildField != null)
            forceShadowRebuildField.SetValue(caster, true);

        // Turn on Self Shadows (castsShadows) to seamlessly fix URP 2D shadow surface Z-fighting (the flickering) 
        // when projecting shadows onto the exact same layer as the caster's own MeshRenderer.
        caster.castsShadows = true;
    }

    /// <summary>
    /// Write a simple 4-vertex rectangle shadow shape to a ShadowCaster2D.
    /// Used by ChunkLODManager as a cheap proxy shadow for distant chunks above
    /// the player — solid ground blocks all light, so a full rectangle is correct.
    /// Coordinates are in LOCAL space of the ShadowCaster2D's transform.
    /// </summary>
    public static void SetProxyShadowRect(ShadowCaster2D caster, float minX, float minY, float maxX, float maxY)
    {
        if (caster == null) return;

        // Ensure reflection is initialized (safe to call multiple times)
        if (!reflectionInitialized)
        {
            var type = typeof(ShadowCaster2D);
            shapePathField = type.GetField("m_ShapePath", BindingFlags.NonPublic | BindingFlags.Instance);
            shadowCastingSourceField = type.GetField("m_ShadowCastingSource", BindingFlags.NonPublic | BindingFlags.Instance);
            shapePathHashField = type.GetField("m_ShapePathHash", BindingFlags.NonPublic | BindingFlags.Instance);
            forceShadowRebuildField = type.GetField("m_ForceShadowMeshRebuild", BindingFlags.NonPublic | BindingFlags.Instance);
            reflectionInitialized = true;
        }

        if (shapePathField == null) return;

        try
        {
            if (shadowCastingSourceField != null)
                shadowCastingSourceField.SetValue(caster, System.Enum.ToObject(shadowCastingSourceField.FieldType, 1));

            Vector3[] pts = new Vector3[4]
            {
                new Vector3(minX, minY, 0f),
                new Vector3(maxX, minY, 0f),
                new Vector3(maxX, maxY, 0f),
                new Vector3(minX, maxY, 0f),
            };

            int hash = minX.GetHashCode() ^ minY.GetHashCode() ^ maxX.GetHashCode() ^ maxY.GetHashCode();
            hash ^= 0x50524F58; // "PROX" marker

            shapePathField.SetValue(caster, pts);
            if (shapePathHashField != null)
                shapePathHashField.SetValue(caster, hash);
            if (forceShadowRebuildField != null)
                forceShadowRebuildField.SetValue(caster, true);

            caster.castsShadows = true;
        }
        catch { /* Suppress reflection errors */ }
    }

    /// <summary>
    /// Blend the shadow shape between the real detailed cave outline (t=0) and
    /// a solid chunk rectangle (t=1). Uses Clipper.InflatePaths to progressively
    /// seal cave openings as t increases, creating an organic "fog of war" reveal.
    /// Small features seal first, large caverns last.
    /// </summary>
    public void SetBlendedShadow(float t, float chunkMinX, float chunkMinY, float chunkMaxX, float chunkMaxY)
    {
        if (cachedShadowCaster == null) cachedShadowCaster = GetComponent<ShadowCaster2D>();
        if (cachedShadowCaster == null) return;

        // Endpoints: use fast-path methods
        if (t <= 0.01f)
        {
            SyncShadowCaster();
            return;
        }
        if (t >= 0.99f)
        {
            SetProxyShadowRect(cachedShadowCaster, chunkMinX, chunkMinY, chunkMaxX, chunkMaxY);
            return;
        }

        PathsD sourcePaths = GetInsetPaths();
        if (sourcePaths == null || sourcePaths.Count == 0)
        {
            SetProxyShadowRect(cachedShadowCaster, chunkMinX, chunkMinY, chunkMaxX, chunkMaxY);
            return;
        }

        // Inflate the terrain paths outward to fill caves.
        // Max inflate = half the chunk diagonal, which guarantees full coverage.
        float chunkW = chunkMaxX - chunkMinX;
        float chunkH = chunkMaxY - chunkMinY;
        float maxInflate = Mathf.Sqrt(chunkW * chunkW + chunkH * chunkH) * 0.5f;

        // Use an ease-in curve so small features close first, big caverns stay longer
        float easedT = t * t;
        float inflateAmount = easedT * maxInflate;

        PathsD inflated = Clipper.InflatePaths(sourcePaths, inflateAmount,
            Clipper2Lib.JoinType.Round, Clipper2Lib.EndType.Polygon, 2, 2);

        if (inflated == null || inflated.Count == 0)
        {
            SetProxyShadowRect(cachedShadowCaster, chunkMinX, chunkMinY, chunkMaxX, chunkMaxY);
            return;
        }

        // Clip to chunk bounds so the inflated shape doesn't bleed into neighbors
        PathD chunkRect = new PathD(4);
        chunkRect.Add(new PointD(chunkMinX, chunkMinY));
        chunkRect.Add(new PointD(chunkMaxX, chunkMinY));
        chunkRect.Add(new PointD(chunkMaxX, chunkMaxY));
        chunkRect.Add(new PointD(chunkMinX, chunkMaxY));

        PathsD clipped = Clipper.Intersect(inflated, new PathsD { chunkRect },
            FillRule.NonZero, 2);

        if (clipped == null || clipped.Count == 0)
        {
            SetProxyShadowRect(cachedShadowCaster, chunkMinX, chunkMinY, chunkMaxX, chunkMaxY);
            return;
        }

        // Simplify the blended shape for performance
        clipped = Clipper.SimplifyPaths(clipped, shadowSimplifyTolerance, false);

        // Find the largest path
        PathD largestPath = null;
        double largestArea = 0;
        foreach (var p in clipped)
        {
            if (p == null || p.Count < 3) continue;
            double a = System.Math.Abs(Clipper.Area(p));
            if (a > largestArea) { largestArea = a; largestPath = p; }
        }

        if (largestPath == null)
        {
            SetProxyShadowRect(cachedShadowCaster, chunkMinX, chunkMinY, chunkMaxX, chunkMaxY);
            return;
        }

        // Cap vertices
        if (largestPath.Count > maxShadowVertices)
        {
            float tol = shadowSimplifyTolerance * 2f;
            for (int attempt = 0; attempt < 4 && largestPath.Count > maxShadowVertices; attempt++)
            {
                var simplified = Clipper.SimplifyPaths(
                    new PathsD { largestPath }, tol, false);
                if (simplified != null && simplified.Count > 0 && simplified[0].Count >= 3)
                    largestPath = simplified[0];
                tol *= 2f;
            }
        }

        SetShadowPath(cachedShadowCaster, largestPath, 0);
    }

    /// <summary>
    /// Force-restore the real detailed shadow path from myPaths.
    /// Called by ChunkLODManager when a chunk transitions back from proxy/blended shadows.
    /// </summary>
    public void ForceSyncShadow()
    {
        SyncShadowCaster();
    }

    private void UpdateShadowCasterVisibility()
    {
        if (suppressShadowVisibility) return;

        if (cachedShadowCaster == null) cachedShadowCaster = GetComponent<ShadowCaster2D>();
        var sc = cachedShadowCaster;
        if (sc == null) return;

        // BYPASS: Frustum culling ShadowCaster2Ds breaks directional lighting, 
        // because an off-screen chunk's shadow could be reaching deep into the visible screen.
        // URP 2D relies on its own internal light-bounded culling trees which are extremely fast.
        if (sc.enabled != true)
        {
            sc.enabled = true;
            SyncShadowCaster();
        }
    }

    private bool IsChunkInView()
    {
        // Cache frustum planes once per frame across all chunks.
        int frame = Time.frameCount;
        if (frame != cachedCameraFrame)
        {
            cachedCamera = Camera.main;
            if (cachedCamera != null)
                GeometryUtility.CalculateFrustumPlanes(cachedCamera, cachedFrustumPlanes);
            cachedCameraFrame = frame;
        }

        if (cachedCamera == null) return true;

        // Expand bounds to account for shadow reach beyond the visible mesh.
        // Use hysteresis: larger margin when currently visible to prevent flickering.
        float margin = shadowInset + 5f + (wasInView ? CullingHysteresisMargin : 0f);
        Bounds padded = chunkWorldBounds;
        padded.Expand(margin);
        bool inView = GeometryUtility.TestPlanesAABB(cachedFrustumPlanes, padded);
        wasInView = inView;
        return inView;
    }

    private Bounds ComputeWorldBoundsFromPaths()
    {
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in myPaths)
        {
            foreach (var pt in p)
            {
                float px = (float)pt.x;
                float py = (float)pt.y;
                if (px < minX) minX = px;
                if (px > maxX) maxX = px;
                if (py < minY) minY = py;
                if (py > maxY) maxY = py;
            }
        }
        Vector3 center = transform.TransformPoint(new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, 0f));
        Vector3 size = new Vector3(maxX - minX, maxY - minY, 0.1f);
        boundsInitialized = true;
        return new Bounds(center, size);
    }

    private void SyncCollider()
    {
        if (myPaths == null) return;
        if (col == null) col = GetComponent<PolygonCollider2D>();
        if (col == null) return;
        col.pathCount = myPaths.Count;
        for (int i = 0; i < myPaths.Count; i++)
        {
            Vector2[] pts = new Vector2[myPaths[i].Count];
            for (int j = 0; j < myPaths[i].Count; j++)
                pts[j] = new Vector2((float)myPaths[i][j].x, (float)myPaths[i][j].y);
            col.SetPath(i, pts);
        }
    }

    /// <summary>
    /// Compute the inset paths used for both the wall mesh and shadow shape.
    /// Cached so InflatePaths only runs once per path change, not per consumer.
    /// Returns myPaths unchanged when shadowInset is zero or this is a foreground chunk.
    /// </summary>
    private PathsD GetInsetPaths()
    {
        if (shadowInset <= 0f || myPaths == null || myPaths.Count == 0)
            return myPaths;

        if (cachedInsetPaths == null)
        {
            PathsD insetPaths = Clipper.InflatePaths(myPaths, -shadowInset,
                Clipper2Lib.JoinType.Round, Clipper2Lib.EndType.Polygon, 2, 2);
            cachedInsetPaths = (insetPaths != null && insetPaths.Count > 0) ? insetPaths : myPaths;
        }
        return cachedInsetPaths;
    }

    private void UpdateMesh()
    {
        if (myPaths == null || myPaths.Count == 0)
        {
            if (meshFilter) meshFilter.sharedMesh = null;
            return;
        }

        // Wall chunks use the inset shape for their visible mesh so the
        // light-bleed zone at cave edges isn't covered by the wall.
        PathsD meshPaths = (!skipShadowCaster && shadowInset > 0f) ? GetInsetPaths() : myPaths;

        if (Clipper.Triangulate(meshPaths, 2, out PathsD triangles) != TriangulateResult.success)
        {
            Debug.LogWarning($"[Chunk {name}] Triangulation failed for {myPaths.Count} paths.");
            return;
        }

        if (triangles.Count == 0)
        {
            Debug.LogWarning($"[Chunk {name}] Triangulate success but 0 triangles returned.");
        }

        int triCount = triangles.Count;
        Vector3[] vertices = new Vector3[triCount * 3];
        int[] indices = new int[triCount * 3];
        Vector2[] uvs = new Vector2[triCount * 3];
        Color[] colors = new Color[triCount * 3];

        for (int i = 0; i < triCount; i++)
        {
            PathD tri = triangles[i];
            if (tri.Count < 3) continue;

            bool isPositive = Clipper.IsPositive(tri);

            for (int v = 0; v < 3; v++)
            {
                int idx = i * 3 + v;
                int vIdx = isPositive ? (2 - v) : v;

                float px = (float)tri[vIdx].x;
                float py = (float)tri[vIdx].y;
                vertices[idx] = new Vector3(px, py, meshZOffset);
                uvs[idx] = new Vector2(px * 0.2f, py * 0.2f);
                indices[idx] = idx;
                colors[idx] = terrainColor;
            }
        }

        if (!cachedMesh)
        {
            cachedMesh = new Mesh();
            cachedMesh.name = "ChunkMesh_" + name;
            cachedMesh.MarkDynamic();
        }
        cachedMesh.Clear();
        cachedMesh.vertices = vertices;
        cachedMesh.triangles = indices;
        cachedMesh.uv = uvs;
        cachedMesh.colors = colors;
        cachedMesh.RecalculateBounds();
        cachedMesh.RecalculateNormals();
        meshFilter.sharedMesh = cachedMesh;

        // Update world bounds for frustum culling.
        chunkWorldBounds = meshRenderer.bounds;
        boundsInitialized = true;
    }

    private void OnDestroy()
    {
        if (cachedMesh) Destroy(cachedMesh);
    }
}
