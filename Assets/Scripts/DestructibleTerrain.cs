using UnityEngine;
using UnityEngine.Rendering.Universal;
using Clipper2Lib;
using System.Collections.Generic;

[RequireComponent(typeof(PolygonCollider2D))]
public class DestructibleTerrain : MonoBehaviour
{
    [Header("Visuals")]
    public Color terrainColor = new Color(0.35f, 0.22f, 0.13f, 1f);
    public Material customMaterial;

    [Header("Layer Sorting")]
    public string sortingLayerName = "Default";
    public int sortingOrder = 0;

    [Header("Shadow Caster (Surface Only)")]
    [Tooltip("Creates a child shadow proxy that casts shadows from the terrain surface profile")]
    public bool enableShadowCaster = true;
    [Tooltip("Minimum seconds between shadow updates during mining")]
    public float shadowUpdateCooldown = 0.5f;

    private PolygonCollider2D col;
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private Mesh cachedMesh;
    private float lastShadowUpdateTime = -999f;

    // Shadow proxy
    private GameObject shadowProxy;
    private PolygonCollider2D shadowCollider;
    private ShadowCaster2D shadowCaster;

    // Surface profile from ProceduralTerrainGenerator
    private Vector2[] surfaceProfile;
    private float terrainHeight;

    void Start()
    {
        col = GetComponent<PolygonCollider2D>();
        EnsureVisuals();
        UpdateMesh();
        UpdateShadowCaster();
    }

    /// <summary>
    /// Called by ProceduralTerrainGenerator after generation.
    /// Stores the smooth surface profile for shadow casting.
    /// </summary>
    public void SetSurfaceProfile(Vector2[] profile, float height)
    {
        surfaceProfile = profile;
        terrainHeight = height;
    }

    #region Visual Mesh Setup

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

    #endregion

    #region Terrain Modification

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

        if (enableShadowCaster && Time.time - lastShadowUpdateTime > shadowUpdateCooldown)
        {
            UpdateShadowCaster();
            lastShadowUpdateTime = Time.time;
        }
    }

    public void UpdateMesh()
    {
        EnsureVisuals();
        if (col == null || meshFilter == null) return;

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

    #endregion

    #region Shadow Proxy (Surface Only)

    /// <summary>
    /// Builds a shadow caster from the stored surface profile.
    /// The polygon is: bottom-left → surface (left to right) → bottom-right.
    /// This creates a solid shape where only the surface edge casts visible shadows,
    /// blocking light from penetrating underground.
    /// </summary>
    public void UpdateShadowCaster()
    {
        if (!enableShadowCaster)
        {
            DestroyShadowProxy();
            return;
        }

        if (surfaceProfile == null || surfaceProfile.Length < 2) return;

        EnsureShadowProxy();

        Vector2[] shape = BuildSurfaceShadowShape();
        if (shape == null || shape.Length < 3)
        {
            shadowCollider.pathCount = 0;
            return;
        }

        shadowCollider.pathCount = 1;
        shadowCollider.SetPath(0, shape);
    }

    /// <summary>
    /// Builds a thin polygon strip along the surface profile.
    /// The bottom sits just below the lowest surface point so the terrain mesh
    /// hides the bottom/side edges. Only the surface edge casts visible shadows.
    /// </summary>
    private Vector2[] BuildSurfaceShadowShape()
    {
        float minX = surfaceProfile[0].x;
        float maxX = surfaceProfile[surfaceProfile.Length - 1].x;

        // Find the lowest surface point and extend just below it
        float lowestY = surfaceProfile[0].y;
        for (int i = 1; i < surfaceProfile.Length; i++)
        {
            if (surfaceProfile[i].y < lowestY)
                lowestY = surfaceProfile[i].y;
        }
        float bottomY = lowestY - 1f; // Thin strip — hidden behind terrain mesh

        // Shape: bottom-left, surface points (left to right), bottom-right
        // Winding: up → right → down → left = counter-clockwise
        Vector2[] shape = new Vector2[surfaceProfile.Length + 2];

        shape[0] = new Vector2(minX, bottomY);                     // bottom-left

        for (int i = 0; i < surfaceProfile.Length; i++)
            shape[i + 1] = surfaceProfile[i];                     // surface, left to right

        shape[surfaceProfile.Length + 1] = new Vector2(maxX, bottomY); // bottom-right

        return shape;
    }

    private void EnsureShadowProxy()
    {
        if (shadowProxy != null && shadowCollider != null && shadowCaster != null) return;

        Transform existing = transform.Find("ShadowProxy");
        if (existing != null)
        {
            shadowProxy = existing.gameObject;
        }
        else
        {
            shadowProxy = new GameObject("ShadowProxy");
            shadowProxy.transform.SetParent(transform, false);
            shadowProxy.transform.localPosition = Vector3.zero;
            shadowProxy.transform.localRotation = Quaternion.identity;
            shadowProxy.transform.localScale = Vector3.one;
            shadowProxy.layer = gameObject.layer;
        }

        shadowCollider = shadowProxy.GetComponent<PolygonCollider2D>();
        if (shadowCollider == null)
            shadowCollider = shadowProxy.AddComponent<PolygonCollider2D>();
        shadowCollider.isTrigger = true;

        shadowCaster = shadowProxy.GetComponent<ShadowCaster2D>();
        if (shadowCaster == null)
            shadowCaster = shadowProxy.AddComponent<ShadowCaster2D>();
    }

    private void DestroyShadowProxy()
    {
        if (shadowProxy == null) return;

        if (Application.isPlaying)
            Destroy(shadowProxy);
        else
            DestroyImmediate(shadowProxy);

        shadowProxy = null;
        shadowCollider = null;
        shadowCaster = null;
    }

    #endregion

    #region Cleanup

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

    void OnDisable()
    {
        if (cachedMesh != null && Application.isPlaying)
        {
            Destroy(cachedMesh);
            cachedMesh = null;
        }
    }

    #endregion
}
