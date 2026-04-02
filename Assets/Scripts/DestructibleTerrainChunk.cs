using UnityEngine;
using Clipper2Lib;

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
            meshRenderer.sharedMaterial = new Material(Shader.Find("Sprites/Default"));

        meshRenderer.sortingLayerName = layer;
        meshRenderer.sortingOrder = order;

        MaterialPropertyBlock block = new MaterialPropertyBlock();
        meshRenderer.GetPropertyBlock(block);
        block.SetColor("_Color", terrainColor);
        block.SetColor("_BaseColor", terrainColor);
        meshRenderer.SetPropertyBlock(block);

        SyncCollider();
        UpdateMesh();
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

        PathD clipPath = new PathD(circleSegments);
        for (int i = circleSegments; i >= 0; i--)
        {
            float angle = (i * Mathf.PI * 2f) / circleSegments;
            clipPath.Add(new PointD(
                Mathf.Cos(angle) * radius + localPos.x,
                Mathf.Sin(angle) * radius + localPos.y
            ));
        }

        int pathsBefore = myPaths.Count;
        PathsD result = Clipper.Difference(myPaths, new PathsD { clipPath }, FillRule.NonZero, 2);

        if (result == null || result.Count == 0)
        {
            Debug.LogWarning($"[Chunk {name}] Clipper.Difference returned {result?.Count ?? 0} paths (had {pathsBefore}). Clip at {localPos}, r={radius}");
            // Don't wipe myPaths — keep existing geometry
            return;
        }

        myPaths = result;
        SyncCollider();
        dirty = true;
    }

    private void LateUpdate()
    {
        if (dirty)
        {
            UpdateMesh();
            dirty = false;
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

                vertices[idx] = new Vector3((float)tri[vIdx].x, (float)tri[vIdx].y, 0);
                uvs[idx] = new Vector2(vertices[idx].x * 0.2f, vertices[idx].y * 0.2f);
                colors[idx] = terrainColor;
                indices[idx] = idx;
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

    private void OnDestroy()
    {
        if (cachedMesh) Destroy(cachedMesh);
    }
}
