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
    private static FieldInfo pathCountField;
    private static bool reflectionInitialized = false;

    // Depth-based vertex darkening settings
    private float fogFadeEnd = 5f;
    private float fogOpacity = 0.95f;
    private float fogAggressiveness = 1f;
    private bool fogEnabled = true;
    private Vector2 sunDirection = new Vector2(0.5f, -1f);
    private Vector2 sunOrigin = Vector2.zero;
    private float sunMinProjection = 0f;
    private float sunMaxProjection = 1f;

    public void SetFogSettings(float fadeDistance, float opacity)
    {
        fogFadeEnd = fadeDistance;
        fogOpacity = opacity;
    }

    public void SetFogAggressiveness(float aggressiveness)
    {
        fogAggressiveness = Mathf.Max(0.01f, aggressiveness);
    }

    public void SetFogEnabled(bool enabled)
    {
        fogEnabled = enabled;
    }

    public void SetSunDirection(Vector2 direction)
    {
        if (direction.sqrMagnitude < 0.0001f) direction = new Vector2(0.5f, -1f);
        sunDirection = direction.normalized;
    }

    public void SetSunOrigin(Vector2 origin)
    {
        sunOrigin = origin;
    }

    public void SetSunProjectionRange(float minProjection, float maxProjection)
    {
        sunMinProjection = minProjection;
        sunMaxProjection = maxProjection;
    }

    public void Initialize(PathsD paths, Color color, Material mat, string layer, int order)
    {
        myPaths = paths;
        terrainColor = color;

        col = gameObject.GetComponent<PolygonCollider2D>();
        if (!col) col = gameObject.AddComponent<PolygonCollider2D>();

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

        SyncCollider();
        UpdateMesh();

        // Add per-chunk shadow caster — uses the PolygonCollider2D as shape source
        var shadowCaster = gameObject.GetComponent<ShadowCaster2D>();
        if (!shadowCaster) shadowCaster = gameObject.AddComponent<ShadowCaster2D>();
    }

    /// <summary>
    /// Lazily ensure myPaths and component refs are valid.
    /// Handles the case where the chunk was serialized in the scene but
    /// myPaths (non-serialized) is null — rebuild from the PolygonCollider2D.
    /// </summary>
    private void EnsureReady()
    {
        if (col == null) col = GetComponent<PolygonCollider2D>();
        if (meshFilter == null) meshFilter = GetComponent<MeshFilter>();
        if (meshRenderer == null) meshRenderer = GetComponent<MeshRenderer>();

        // Recover terrain color from the parent manager (it's serialized there)
        if (terrainColor.a < 0.01f)
        {
            var terrain = GetComponentInParent<DestructibleTerrain>();
            if (terrain != null)
                terrainColor = terrain.terrainColor;
            else
                terrainColor = new Color(0.35f, 0.22f, 0.13f, 1f);
        }

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
    }

    public void MineHole(Vector2 localPos, float radius, int circleSegments)
    {
        EnsureReady();
        if (myPaths == null || myPaths.Count == 0) return;

        bool changed = false;

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

        if (result != null && result.Count > 0 && result.Count <= myPaths.Count + 10) // Basic sanity check
        {
            myPaths = Clipper.SimplifyPaths(result, 0.05, false); 
            changed = true;
        }

        if (!changed) return;

        // Propagate digging to any wall chunks (children)
        foreach (Transform child in transform)
        {
            var childChunk = child.GetComponent<DestructibleTerrainChunk>();
            if (childChunk != null)
                childChunk.MineHole(localPos, radius, circleSegments);
        }

        dirty = true;
    }

    private void LateUpdate()
    {
        if (dirty)
        {
            SyncCollider();
            SyncShadowCaster();
            UpdateMesh();
            dirty = false;
        }
    }

    private void SyncShadowCaster()
    {
        var sc = GetComponent<ShadowCaster2D>();
        if (sc == null || myPaths == null) return;

        if (!reflectionInitialized)
        {
            var type = typeof(ShadowCaster2D);
            shapePathField = type.GetField("m_ShapePath", BindingFlags.NonPublic | BindingFlags.Instance);
            pathCountField = type.GetField("m_PathCount", BindingFlags.NonPublic | BindingFlags.Instance);
            reflectionInitialized = true;
        }

        if (shapePathField != null)
        {
            try {
                int totalPoints = 0;
                foreach (var p in myPaths) totalPoints += p.Count;
                
                Vector3[] pts = new Vector3[totalPoints];
                int idx = 0;
                foreach (var p in myPaths)
                {
                    foreach (var pt in p)
                    {
                        pts[idx++] = new Vector3((float)pt.x, (float)pt.y, 0f);
                    }
                }
                shapePathField.SetValue(sc, pts);
                if (pathCountField != null) pathCountField.SetValue(sc, myPaths.Count);
            } catch { /* Suppress */ }
        }
    }

    private void SyncCollider()
    {
        if (myPaths == null) return;
        col.pathCount = myPaths.Count;
        for (int i = 0; i < myPaths.Count; i++)
        {
            Vector2[] pts = new Vector2[myPaths[i].Count];
            for (int j = 0; j < myPaths[i].Count; j++)
                pts[j] = new Vector2((float)myPaths[i][j].x, (float)myPaths[i][j].y);
            col.SetPath(i, pts);
        }
    }

    private void UpdateMesh()
    {
        if (myPaths == null || myPaths.Count == 0)
        {
            if (meshFilter) meshFilter.sharedMesh = null;
            return;
        }

        if (Clipper.Triangulate(myPaths, 2, out PathsD triangles) != TriangulateResult.success)
        {
            Debug.LogWarning($"[Chunk {name}] Triangulation failed for {myPaths.Count} paths.");
            return;
        }

        if (triangles.Count == 0)
        {
            Debug.LogWarning($"[Chunk {name}] Triangulate success but 0 triangles returned.");
        }

        // Build edge segments for distance-to-boundary calculation
        int totalSegments = 0;
        foreach (var path in myPaths)
            totalSegments += path.Count;

        Vector2[] segA = new Vector2[totalSegments];
        Vector2[] segB = new Vector2[totalSegments];
        int segIdx = 0;
        foreach (var path in myPaths)
        {
            int count = path.Count;
            for (int s = 0; s < count; s++)
            {
                int next = (s + 1) % count;
                segA[segIdx] = new Vector2((float)path[s].x, (float)path[s].y);
                segB[segIdx] = new Vector2((float)path[next].x, (float)path[next].y);
                segIdx++;
            }
        }

        float invFadeEnd = fogFadeEnd > 0f ? 1f / fogFadeEnd : 1f;
        Vector2 lightDir = -sunDirection.normalized;

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
                vertices[idx] = new Vector3(px, py, 0);
                uvs[idx] = new Vector2(px * 0.2f, py * 0.2f);
                indices[idx] = idx;

                float brightness = 1f;
                if (fogEnabled)
                {
                    // Compute distance to nearest polygon edge segment
                    float minDistSqr = float.MaxValue;
                    for (int e = 0; e < totalSegments; e++)
                    {
                        float distSqr = SqrDistToSegment(px, py, segA[e], segB[e]);
                        if (distSqr < minDistSqr) minDistSqr = distSqr;
                    }

                    // 0 = on edge (full brightness), 1 = deep (darkened toward black)
                    float depth = Mathf.Clamp01(Mathf.Sqrt(minDistSqr) * invFadeEnd);
                    depth = Mathf.Pow(depth, fogAggressiveness);
                    Vector2 worldPos = new Vector2(px, py);
                    float projection = Vector2.Dot(worldPos - sunOrigin, -lightDir);
                    float directional = Mathf.InverseLerp(sunMinProjection, sunMaxProjection, projection);
                    brightness = Mathf.Clamp01((1f - depth * fogOpacity) * (0.6f + directional * 0.4f));
                }

                // Vertex colors always carry the terrain tint directly.
                colors[idx] = new Color(
                    terrainColor.r * brightness,
                    terrainColor.g * brightness,
                    terrainColor.b * brightness,
                    1f);
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
    }

    /// <summary>
    /// Squared distance from point (px,py) to the closest point on line segment AB.
    /// </summary>
    private static float SqrDistToSegment(float px, float py, Vector2 a, Vector2 b)
    {
        float abx = b.x - a.x;
        float aby = b.y - a.y;
        float apx = px - a.x;
        float apy = py - a.y;

        float dot = apx * abx + apy * aby;
        float lenSqr = abx * abx + aby * aby;

        // t = projection of P onto AB, clamped to [0,1]
        float t = lenSqr > 0f ? Mathf.Clamp01(dot / lenSqr) : 0f;

        float closestX = a.x + t * abx - px;
        float closestY = a.y + t * aby - py;
        return closestX * closestX + closestY * closestY;
    }

    private void OnDestroy()
    {
        if (cachedMesh) Destroy(cachedMesh);
    }
}
