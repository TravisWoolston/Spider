using UnityEngine;
using Clipper2Lib;

[RequireComponent(typeof(PolygonCollider2D))]
public class DestructibleTerrain : MonoBehaviour
{
    [Header("Visuals")]
    public Color terrainColor = new Color(0.35f, 0.22f, 0.13f, 1f);
    public Material customMaterial;

    [Header("Layer Sorting")]
    public string sortingLayerName = "Default";
    public int sortingOrder = 0;

    private PolygonCollider2D col;
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;

    // CRITICAL: Track the mesh to prevent memory leaks
    private Mesh cachedMesh;

    void Start()
    {
        col = GetComponent<PolygonCollider2D>();
        EnsureVisuals();
        UpdateMesh();
    }

    public void EnsureVisuals()
    {
        if (col == null) col = GetComponent<PolygonCollider2D>();

        SpriteRenderer sr = GetComponent<SpriteRenderer>();
        if (sr != null)
        {
            sortingLayerName = sr.sortingLayerName;
            sortingOrder = sr.sortingOrder;
            sr.enabled = false;
        }

        Transform child = transform.Find("CleanTerrainMesh");
        if (child == null)
        {
            GameObject visualChild = new GameObject("CleanTerrainMesh");
            visualChild.transform.SetParent(transform, false);
            visualChild.transform.localPosition = Vector3.zero;
            visualChild.transform.localRotation = Quaternion.identity;
            visualChild.layer = gameObject.layer;
            child = visualChild.transform;
        }

        meshFilter = child.GetComponent<MeshFilter>();
        if (meshFilter == null) meshFilter = child.gameObject.AddComponent<MeshFilter>();

        meshRenderer = child.GetComponent<MeshRenderer>();
        if (meshRenderer == null) meshRenderer = child.gameObject.AddComponent<MeshRenderer>();

        meshRenderer.sortingLayerName = sortingLayerName;
        meshRenderer.sortingOrder = sortingOrder;

        if (meshRenderer.sharedMaterial == null)
        {
            if (customMaterial != null)
            {
                meshRenderer.sharedMaterial = customMaterial;
            }
            else
            {
                Shader shader = Shader.Find("Sprites/Default");
                if (shader == null) shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Lit-Default");
                if (shader == null) shader = Shader.Find("Unlit/Color");

                meshRenderer.sharedMaterial = new Material(shader);
            }
        }

        MaterialPropertyBlock block = new MaterialPropertyBlock();
        meshRenderer.GetPropertyBlock(block);
        if (meshRenderer.sharedMaterial.mainTexture == null)
        {
            block.SetTexture("_MainTex", Texture2D.whiteTexture);
            block.SetTexture("_BaseMap", Texture2D.whiteTexture);
        }
        block.SetColor("_Color", terrainColor);
        block.SetColor("_BaseColor", terrainColor);
        meshRenderer.SetPropertyBlock(block);
    }

    public void MineHole(Vector2 worldPos, float radius, int circleSegments = 16)
    {
        EnsureVisuals();
        Vector2 localPos = transform.InverseTransformPoint(worldPos);

        PathD clipPath = new PathD();
        for (int i = circleSegments; i >= 0; i--)
        {
            float angle = (i * Mathf.PI * 2f) / circleSegments;
            Vector2 p = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius + localPos;
            clipPath.Add(new PointD(p.x, p.y));
        }
        PathsD clipPaths = new PathsD() { clipPath };

        PathsD subjectPaths = new PathsD();
        for (int i = 0; i < col.pathCount; i++)
        {
            Vector2[] currentPath = col.GetPath(i);
            PathD path = new PathD();
            foreach (Vector2 p in currentPath)
                path.Add(new PointD(p.x, p.y));

            subjectPaths.Add(path);
        }

        PathsD solution = Clipper.Difference(subjectPaths, clipPaths, FillRule.NonZero, 2);

        col.pathCount = solution.Count;
        for (int i = 0; i < solution.Count; i++)
        {
            PathD resultPath = solution[i];
            Vector2[] newPath = new Vector2[resultPath.Count];
            for (int j = 0; j < resultPath.Count; j++)
            {
                newPath[j] = new Vector2((float)resultPath[j].x, (float)resultPath[j].y);
            }
            col.SetPath(i, newPath);
        }

        UpdateMesh();
        UpdateShadowCaster();
    }

    public void UpdateMesh()
    {
        EnsureVisuals();
        if (col == null || meshFilter == null) return;

        // CRITICAL FIX: Destroy old mesh before creating a new one
        if (cachedMesh != null)
        {
            if (Application.isPlaying)
                Destroy(cachedMesh);
            else
                DestroyImmediate(cachedMesh);
            cachedMesh = null;
        }

        Mesh mesh = col.CreateMesh(true, true);

        if (mesh != null)
        {
            Vector3[] vertices = mesh.vertices;
            Vector2[] uvs = new Vector2[vertices.Length];
            Color[] colors = new Color[vertices.Length];
            Vector3[] normals = new Vector3[vertices.Length];

            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 worldPos = vertices[i];
                Vector3 localPos = transform.InverseTransformPoint(worldPos);

                vertices[i] = new Vector3(localPos.x, localPos.y, 0f);
                uvs[i] = new Vector2(worldPos.x * 0.2f, worldPos.y * 0.2f);
                colors[i] = terrainColor;
                normals[i] = new Vector3(0, 0, -1f);
            }

            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.colors = colors;
            mesh.normals = normals;

            int[] triangles = mesh.triangles;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int temp = triangles[i + 1];
                triangles[i + 1] = triangles[i + 2];
                triangles[i + 2] = temp;
            }
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
        }

        cachedMesh = mesh;
        meshFilter.sharedMesh = mesh;
    }

    public void UpdateShadowCaster()
    {
#if UNITY_2020_1_OR_NEWER
        EnsureVisuals();
        var shadowCaster = GetComponent<UnityEngine.Rendering.Universal.ShadowCaster2D>();
        if (shadowCaster == null) return;

        var shapeField = typeof(UnityEngine.Rendering.Universal.ShadowCaster2D).GetField("m_ShapePath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var generateMethod = typeof(UnityEngine.Rendering.Universal.ShadowCaster2D).GetMethod("GenerateShadowMesh", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var meshField = typeof(UnityEngine.Rendering.Universal.ShadowCaster2D).GetField("m_Mesh", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (shapeField != null && generateMethod != null && meshField != null && col.pathCount > 0)
        {
            Vector2[] path2D = col.GetPath(0);
            Vector3[] path3D = new Vector3[path2D.Length];
            for (int i = 0; i < path2D.Length; i++)
                path3D[i] = new Vector3(path2D[i].x, path2D[i].y, 0);

            shapeField.SetValue(shadowCaster, path3D);
            generateMethod.Invoke(shadowCaster, new object[] { meshField.GetValue(shadowCaster), shapeField.GetValue(shadowCaster) });
        }
#endif
    }

    // CRITICAL: Clean up mesh when destroyed
    void OnDestroy()
    {
        if (cachedMesh != null)
        {
            if (Application.isPlaying)
                Destroy(cachedMesh);
            else
                DestroyImmediate(cachedMesh);
            cachedMesh = null;
        }
    }

    // CRITICAL: Clean up mesh when disabled (in case of pooling)
    void OnDisable()
    {
        if (cachedMesh != null && Application.isPlaying)
        {
            Destroy(cachedMesh);
            cachedMesh = null;
        }
    }
}
