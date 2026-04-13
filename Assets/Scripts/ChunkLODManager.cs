using UnityEngine;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;

/// <summary>
/// Manages chunk activation/deactivation based on camera frustum and player proximity.
///
/// Shadow blending uses viewport visibility (how much of the chunk is on-screen)
/// instead of fixed world-unit distances, automatically adapting to zoom and camera position.
///
/// Skylight exposure detection uses Physics2D raycasts to skip shadow casters
/// on chunks buried under solid terrain with no line-of-sight to the sky.
///
/// Attach to the same GameObject as DestructibleTerrain.
/// </summary>
[RequireComponent(typeof(DestructibleTerrain))]
public class ChunkLODManager : MonoBehaviour
{
    public enum ChunkLOD { Full, Reduced, Disabled }

    [Header("Frustum Settings")]
    [Tooltip("Padding around the camera frustum (world units). Chunks within this margin stay Full.")]
    public float frustumPadding = 10f;

    [Header("Shadow Blending — Viewport-Based")]
    [Tooltip("When enabled, shadow blending with distance is active. When disabled, shadows use full detail regardless of distance.")]
    public bool shadowTransitions = true;

    [Tooltip("Viewport visibility (0–1) above which shadows use full cave detail. Below this they blend toward solid rectangles.")]
    [Range(0.1f, 1f)]
    public float shadowFullDetailVisibility = 0.7f;

    [Tooltip("World-unit padding beyond the frustum where off-screen shadow casters remain active as solid rectangles to block light leaking.")]
    public float shadowReachPadding = 20f;

    [Tooltip("Lerp shadow shapes smoothly per-frame to prevent light-switch effect.")]
    public bool smoothShadowTransitions = true;

    [Tooltip("Lerp speed for shadow transitions (units per second).")]
    public float shadowBlendSpeed = 2f;

    [Header("Skylight Exposure")]
    [Tooltip("When enabled, only chunks hit by the sun's raycasts will have their child ShadowCasters enabled.")]
    public bool sunlightOnly = false;

    [Tooltip("The sun/spot light. Raycasts are cast FROM this light toward terrain to determine exposure. Uses position, direction, cone angle, and radius.")]
    public Light2D sunLight;

    [Tooltip("Layer mask for skylight raycasts. Set to your terrain/Dirt layer for best results.")]
    public LayerMask terrainLayerMask = ~0;

    [Tooltip("Ray samples per chunk for skylight detection (distributed across chunk face).")]
    [Range(1, 9)]
    public int skylightSampleCount = 4;

    [Tooltip("Show skylight raycasts in Scene view (green=exposed, red=blocked, yellow=out of range).")]
    public bool drawSkylightRays = true;

    [Header("Player Proximity")]
    [Tooltip("Chunks within this radius of the player keep colliders + shadows active even if off-screen.")]
    public float playerPhysicsRadius = 25f;

    [Header("Hysteresis")]
    [Tooltip("Extra buffer (world units) before downgrading LOD tier (prevents popping)")]
    public float hysteresisMargin = 3f;

    [Header("Performance")]
    [Tooltip("Seconds between LOD evaluation passes")]
    public float updateInterval = 0.2f;

    [Header("References")]
    [Tooltip("The player transform. Auto-detected from SpiderControllerIK if null.")]
    public Transform playerTransform;

    [Tooltip("Camera for frustum checks. Uses Camera.main if null.")]
    public Camera targetCamera;

    // Internal state
    private DestructibleTerrain terrain;
    private List<ChunkEntry> managedChunks = new List<ChunkEntry>();
    private float timer = 0f;
    private bool initialized = false;
    private Plane[] frustumPlanes = new Plane[6];
    private float terrainTopY = 10f;
    private int maxGridY = 0;

    private bool skylightDirty = false;

    // Debug ray visualization
    private struct DebugRay
    {
        public Vector2 origin, endpoint;
        public bool hit; // true = hit terrain (exposed surface), false = missed (open air)
    }
    private List<DebugRay> debugRays = new List<DebugRay>();

    // Stats
    private int fullCount, reducedCount, disabledCount, shadowsActiveCount, blendedShadowCount, buriedCount;

    private struct ChunkEntry
    {
        public Transform chunkTransform;
        public DestructibleTerrainChunk chunk;
        public Bounds worldBounds;
        public ChunkLOD currentLOD;
        public bool boundsValid;
        public bool shadowsOn;
        public float shadowBlendT;       // current (0=detail, 1=solid, -1=inactive)
        public float shadowBlendTarget;  // target computed by LOD pass
        public int gridX, gridY;
        public bool skylightExposed;
        public MeshRenderer meshRenderer;
        public PolygonCollider2D collider;
        public ShadowCaster2D[] shadowCasters;
        public DestructibleTerrainChunk[] wallChunks;
    }

    void Awake() { terrain = GetComponent<DestructibleTerrain>(); }

    void Start()
    {
        if (playerTransform == null)
        {
            // Try SpiderControllerIK first (regular spider player)
            var spider = FindObjectOfType<SpiderControllerIK>();
            if (spider != null && spider.headRb != null)
                playerTransform = spider.headRb.transform;
            else if (spider != null)
                playerTransform = spider.transform;

            // Fallback: try CentipedeIK (centipede player)
            if (playerTransform == null)
            {
                var centipede = FindObjectOfType<CentipedeIK>();
                if (centipede != null && centipede.headRb != null)
                    playerTransform = centipede.headRb.transform;
                else if (centipede != null)
                    playerTransform = centipede.transform;
            }
        }
        if (targetCamera == null) targetCamera = Camera.main;
        Invoke(nameof(Initialize), 0.1f);
    }

    public void Initialize()
    {
        if (terrain == null) terrain = GetComponent<DestructibleTerrain>();
        if (terrain == null) return;

        managedChunks.Clear();
        maxGridY = 0;

        for (int i = 0; i < transform.childCount; i++)
        {
            var child = transform.GetChild(i);
            var chunk = child.GetComponent<DestructibleTerrainChunk>();
            if (chunk == null || child.name.StartsWith("Wall_")) continue;

            var entry = new ChunkEntry
            {
                chunkTransform = child,
                chunk = chunk,
                currentLOD = ChunkLOD.Full,
                shadowsOn = true,
                shadowBlendT = -1f,
                shadowBlendTarget = -1f,
                skylightExposed = true,
                meshRenderer = child.GetComponent<MeshRenderer>(),
                collider = child.GetComponent<PolygonCollider2D>(),
                shadowCasters = child.GetComponentsInChildren<ShadowCaster2D>(true),
            };

            string[] parts = child.name.Split('_');
            if (parts.Length >= 3
                && int.TryParse(parts[1], out int gx)
                && int.TryParse(parts[2], out int gy))
            {
                entry.gridX = gx;
                entry.gridY = gy;
                float cs = terrain.chunkSize;
                Vector3 worldMin = transform.TransformPoint(new Vector3(gx * cs, gy * cs, 0));
                Vector3 worldMax = transform.TransformPoint(new Vector3((gx + 1) * cs, (gy + 1) * cs, 0));
                Vector3 center = (worldMin + worldMax) * 0.5f;
                Vector3 size = new Vector3(Mathf.Abs(worldMax.x - worldMin.x), Mathf.Abs(worldMax.y - worldMin.y), 1f);
                entry.worldBounds = new Bounds(center, size);
                entry.boundsValid = true;
                if (gy > maxGridY) maxGridY = gy;
            }
            else if (entry.meshRenderer != null)
            {
                entry.worldBounds = entry.meshRenderer.bounds;
                entry.boundsValid = true;
            }

            var wallChunkList = new List<DestructibleTerrainChunk>();
            foreach (Transform wallChild in child)
            {
                var wc = wallChild.GetComponent<DestructibleTerrainChunk>();
                if (wc != null) wallChunkList.Add(wc);
            }
            entry.wallChunks = wallChunkList.ToArray();
            managedChunks.Add(entry);
        }

        float cs2 = terrain.chunkSize;
        terrainTopY = transform.TransformPoint(new Vector3(0, (maxGridY + 1) * cs2 + 5f, 0)).y;

        RefreshAllSkylightExposure();
        initialized = true;
        Debug.Log($"[ChunkLODManager] Initialized: {managedChunks.Count} chunks, terrainTop={terrainTopY:F1}");
    }

    void Update()
    {
        if (!initialized || managedChunks.Count == 0 || terrain == null) return;

        float dt = Time.deltaTime;
        float chunkSize = terrain.chunkSize;

        timer += dt;
        bool doFullEval = timer >= updateInterval;
        if (doFullEval)
        {
            timer = 0f;
            if (targetCamera == null) targetCamera = Camera.main;
            if (targetCamera == null) return;
            GeometryUtility.CalculateFrustumPlanes(targetCamera, frustumPlanes);
        }

        Vector3 playerPos = playerTransform != null ? playerTransform.position
            : (targetCamera != null ? targetCamera.transform.position : Vector3.zero);

        fullCount = reducedCount = disabledCount = shadowsActiveCount = blendedShadowCount = buriedCount = 0;

        int chunkCount = managedChunks.Count;
        for (int i = 0; i < chunkCount; i++)
        {
            var entry = managedChunks[i];
            if (entry.chunkTransform == null || !entry.boundsValid) continue;

            bool entryChanged = false;

            // ── LOD + shadow target (on timer) ──
            if (doFullEval)
            {
                // Refresh skylight if terrain was dug
                if (skylightDirty)
                {
                    RefreshAllSkylightExposure();
                    skylightDirty = false;
                    entry = managedChunks[i]; // re-read after bulk update
                }

                ChunkLOD desired = EvaluateLOD(entry, playerPos);
                float targetT = -1f;
                bool shadowsEnabled = true;

                if (sunlightOnly && !entry.skylightExposed)
                {
                    // Buried chunk — no sunlight reaches it, shadow caster is wasted work.
                    // Normal LOD applies (can be Disabled).
                    shadowsEnabled = false;
                }
                else if (sunlightOnly && entry.skylightExposed)
                {
                    // Exposed chunk — shadows always active with full detail.
                    // Never allow Disabled (would deactivate GO and kill shadow casters).
                    shadowsEnabled = true;
                    targetT = -1f;
                    if (desired == ChunkLOD.Disabled)
                        desired = ChunkLOD.Reduced;
                }
                else if (shadowTransitions)
                {
                    // Viewport-based shadow blending
                    float visibility = ComputeViewportVisibility(entry.worldBounds, targetCamera);

                    if (visibility >= shadowFullDetailVisibility)
                    {
                        targetT = -1f; // full detail
                    }
                    else if (visibility > 0.01f)
                    {
                        // Partially visible — blend proportionally
                        targetT = 1f - (visibility / Mathf.Max(shadowFullDetailVisibility, 0.01f));
                    }
                    else
                    {
                        // Fully off-screen — check shadow reach padding
                        Bounds reachBounds = entry.worldBounds;
                        reachBounds.Expand(shadowReachPadding);
                        if (GeometryUtility.TestPlanesAABB(frustumPlanes, reachBounds))
                        {
                            targetT = 1f; // solid rect shadow
                            if (desired == ChunkLOD.Disabled)
                                desired = ChunkLOD.Reduced;
                        }
                        else
                        {
                            shadowsEnabled = false;
                        }
                    }
                }
                else
                {
                    // Shadow transitions disabled — use full detail shadows
                    targetT = -1f;
                }

                // Don't force Reduced just for shadows if shadows are disabled
                if (!shadowsEnabled && desired == ChunkLOD.Reduced)
                {
                    float distToPlayer = Vector3.Distance(entry.worldBounds.ClosestPoint(playerPos), playerPos);
                    if (distToPlayer > playerPhysicsRadius)
                        desired = ChunkLOD.Disabled;
                }

                if (desired != entry.currentLOD || shadowsEnabled != entry.shadowsOn || targetT != entry.shadowBlendTarget)
                {
                    entry.shadowsOn = shadowsEnabled;
                    entry.shadowBlendTarget = targetT;

                    // Manage wall chunk suppression when sunlightOnly toggles shadow state
                    if (sunlightOnly && entry.wallChunks != null)
                        for (int w = 0; w < entry.wallChunks.Length; w++)
                            if (entry.wallChunks[w] != null)
                                entry.wallChunks[w].SetSuppressShadowVisibility(!shadowsEnabled);

                    if (desired != entry.currentLOD)
                    {
                        if (entry.shadowBlendT >= 0f && targetT < 0f)
                            RestoreRealShadows(entry);
                        ApplyLOD(ref entry, desired, chunkSize);
                    }
                    entryChanged = true;
                }
            }

            // ── Per-frame shadow lerp ──
            // Only process shadow blending if shadows are enabled for this chunk
            if (entry.shadowsOn && entry.shadowBlendTarget >= 0f)
            {
                float prev = entry.shadowBlendT < 0f ? 0f : entry.shadowBlendT;
                float next = smoothShadowTransitions
                    ? Mathf.MoveTowards(prev, entry.shadowBlendTarget, shadowBlendSpeed * dt)
                    : entry.shadowBlendTarget;

                if (Mathf.Abs(next - prev) > 0.01f || (entryChanged && Mathf.Abs(next - prev) > 0.001f))
                {
                    entry.shadowBlendT = next;
                    SetShadowCastersEnabled(entry, true);
                    ApplyBlendedShadows(entry, chunkSize);
                    entryChanged = true;
                }
            }
            else if (entry.shadowsOn && entry.shadowBlendT >= 0f)
            {
                float next = smoothShadowTransitions
                    ? Mathf.MoveTowards(entry.shadowBlendT, 0f, shadowBlendSpeed * dt) : 0f;

                if (next <= 0.01f)
                {
                    entry.shadowBlendT = -1f;
                    RestoreRealShadows(entry);
                    entryChanged = true;
                }
                else
                {
                    entry.shadowBlendT = next;
                    ApplyBlendedShadows(entry, chunkSize);
                    entryChanged = true;
                }
            }
            else if (!entry.shadowsOn)
            {
                // Shadows disabled (sunlightOnly mode for non-exposed chunks) - ensure casters are off
                SetShadowCastersEnabled(entry, false);
                if (entry.shadowBlendT >= 0f)
                {
                    entry.shadowBlendT = -1f;
                    entryChanged = true;
                }
            }

            if (entryChanged) managedChunks[i] = entry;

            switch (entry.currentLOD)
            {
                case ChunkLOD.Full: fullCount++; break;
                case ChunkLOD.Reduced: reducedCount++; break;
                case ChunkLOD.Disabled: disabledCount++; break;
            }
            if (entry.shadowsOn && entry.currentLOD != ChunkLOD.Disabled) shadowsActiveCount++;
            if (entry.shadowBlendT > 0f && entry.currentLOD != ChunkLOD.Disabled) blendedShadowCount++;
            if (!entry.skylightExposed) buriedCount++;
        }
    }

    // ─── Viewport Visibility ────────────────────────────────────

    /// <summary>
    /// Returns 0..1 indicating how much of the chunk AABB is visible in the viewport.
    /// </summary>
    private float ComputeViewportVisibility(Bounds worldBounds, Camera cam)
    {
        if (cam == null) return 1f;

        Vector3 vpMin = cam.WorldToViewportPoint(worldBounds.min);
        Vector3 vpMax = cam.WorldToViewportPoint(worldBounds.max);

        if (vpMin.z < 0 && vpMax.z < 0) return 0f;

        float cMinX = Mathf.Min(vpMin.x, vpMax.x), cMaxX = Mathf.Max(vpMin.x, vpMax.x);
        float cMinY = Mathf.Min(vpMin.y, vpMax.y), cMaxY = Mathf.Max(vpMin.y, vpMax.y);

        float overlapW = Mathf.Max(0f, Mathf.Min(cMaxX, 1f) - Mathf.Max(cMinX, 0f));
        float overlapH = Mathf.Max(0f, Mathf.Min(cMaxY, 1f) - Mathf.Max(cMinY, 0f));

        float totalW = cMaxX - cMinX;
        float totalH = cMaxY - cMinY;
        if (totalW < 0.0001f || totalH < 0.0001f) return 0f;

        return (overlapW * overlapH) / (totalW * totalH);
    }

    // ─── Skylight Exposure (Fan-from-Sun) ────────────────────────

    /// <summary>
    /// Fan rays FROM the sun light across its cone. Each ray's first terrain hit
    /// marks that chunk as "lit." Chunks never hit by any ray are buried.
    /// This correctly models opaque terrain blocking sunlight.
    /// </summary>
    private void RefreshAllSkylightExposure()
    {
        debugRays.Clear();

        // Default all chunks to NOT exposed
        for (int i = 0; i < managedChunks.Count; i++)
        {
            var entry = managedChunks[i];
            entry.skylightExposed = false;
            managedChunks[i] = entry;
        }

        if (sunLight == null)
        {
            // No light → conservatively assume all exposed
            for (int i = 0; i < managedChunks.Count; i++)
            {
                var entry = managedChunks[i];
                entry.skylightExposed = true;
                managedChunks[i] = entry;
            }
            return;
        }

        // Build lookup: chunk Transform → index for fast hit matching
        var chunkLookup = new Dictionary<Transform, int>(managedChunks.Count);
        for (int i = 0; i < managedChunks.Count; i++)
            if (managedChunks[i].chunkTransform != null)
                chunkLookup[managedChunks[i].chunkTransform] = i;

        Vector2 lightPos = sunLight.transform.position;
        Vector2 lightDir = sunLight.transform.up;
        float outerRadius = sunLight.pointLightOuterRadius;
        float halfAngle = sunLight.pointLightOuterAngle * 0.5f;
        float cs = terrain != null ? terrain.chunkSize : 10f;

        // Calculate ray count: enough density so adjacent rays are ~half a chunk apart
        // at the far edge of the light's reach
        float arcLen = outerRadius * (halfAngle * 2f * Mathf.Deg2Rad);
        int rayCount = Mathf.Max(skylightSampleCount, Mathf.CeilToInt(arcLen / (cs * 0.4f)));

        // Base angle of the light direction
        float baseAngleDeg = Mathf.Atan2(lightDir.y, lightDir.x) * Mathf.Rad2Deg;

        for (int r = 0; r < rayCount; r++)
        {
            float t = (r + 0.5f) / rayCount;
            float angleDeg = baseAngleDeg - halfAngle + t * halfAngle * 2f;
            float angleRad = angleDeg * Mathf.Deg2Rad;
            Vector2 dir = new Vector2(Mathf.Cos(angleRad), Mathf.Sin(angleRad));

            RaycastHit2D hit = Physics2D.Raycast(lightPos, dir, outerRadius, terrainLayerMask);

            if (hit.collider != null)
            {
                // Mark the hit chunk as exposed
                Transform hitTrans = hit.collider.transform;
                if (chunkLookup.TryGetValue(hitTrans, out int idx))
                {
                    var entry = managedChunks[idx];
                    entry.skylightExposed = true;
                    managedChunks[idx] = entry;
                }

                debugRays.Add(new DebugRay
                {
                    origin = lightPos,
                    endpoint = hit.point,
                    hit = true
                });
            }
            else
            {
                // Ray missed all terrain (open air)
                debugRays.Add(new DebugRay
                {
                    origin = lightPos,
                    endpoint = lightPos + dir * outerRadius,
                    hit = false
                });
            }
        }

        int buried = 0;
        for (int i = 0; i < managedChunks.Count; i++)
        {
            var entry = managedChunks[i];
            if (!entry.skylightExposed)
            {
                buried++;
                // When sunlightOnly is enabled, immediately disable shadow casters for unexposed chunks
                if (sunlightOnly)
                {
                    entry.shadowsOn = false;
                    SetShadowCastersEnabled(entry, false);
                    // Suppress wall chunk's UpdateShadowCasterVisibility so it doesn't re-enable
                    if (entry.wallChunks != null)
                        for (int w = 0; w < entry.wallChunks.Length; w++)
                            if (entry.wallChunks[w] != null)
                                entry.wallChunks[w].SetSuppressShadowVisibility(true);
                    managedChunks[i] = entry;
                }
            }
            else if (sunlightOnly && !entry.shadowsOn)
            {
                // Re-enable shadows for exposed chunks that were previously disabled
                entry.shadowsOn = true;
                SetShadowCastersEnabled(entry, true);
                // Allow wall chunk's own visibility management again
                if (entry.wallChunks != null)
                    for (int w = 0; w < entry.wallChunks.Length; w++)
                        if (entry.wallChunks[w] != null)
                            entry.wallChunks[w].SetSuppressShadowVisibility(false);
                managedChunks[i] = entry;
            }
        }

        Debug.Log($"[ChunkLODManager] Skylight fan: {rayCount} rays, {buried}/{managedChunks.Count} buried.");
    }

    /// <summary>
    /// Called by DestructibleTerrain after MineHole. Flags a full skylight
    /// re-scan since digging can expose previously buried chunks.
    /// </summary>
    public void OnChunksDug(Vector2 worldPos, float radius)
    {
        if (!initialized) return;
        skylightDirty = true;
    }

    // ─── LOD Evaluation ─────────────────────────────────────────

    private ChunkLOD EvaluateLOD(ChunkEntry entry, Vector3 playerPos)
    {
        Bounds paddedBounds = entry.worldBounds;
        paddedBounds.Expand(frustumPadding);
        bool inFrustum = GeometryUtility.TestPlanesAABB(frustumPlanes, paddedBounds);

        float distToPlayer = Vector3.Distance(entry.worldBounds.ClosestPoint(playerPos), playerPos);

        float margin = 0f;
        if (entry.currentLOD == ChunkLOD.Full) margin = hysteresisMargin;
        else if (entry.currentLOD == ChunkLOD.Reduced) margin = hysteresisMargin * 0.5f;

        bool inExpandedFrustum = inFrustum;
        if (!inFrustum && margin > 0f)
        {
            Bounds expandedBounds = entry.worldBounds;
            expandedBounds.Expand(frustumPadding + margin);
            inExpandedFrustum = GeometryUtility.TestPlanesAABB(frustumPlanes, expandedBounds);
        }

        if (inExpandedFrustum) return ChunkLOD.Full;
        if (distToPlayer <= playerPhysicsRadius + margin) return ChunkLOD.Reduced;
        return ChunkLOD.Disabled;
    }

    // ─── LOD / Shadow Application ───────────────────────────────

    private void ApplyLOD(ref ChunkEntry entry, ChunkLOD lod, float chunkSize = 10f)
    {
        entry.currentLOD = lod;
        switch (lod)
        {
            case ChunkLOD.Full:
                if (!entry.chunkTransform.gameObject.activeSelf)
                    entry.chunkTransform.gameObject.SetActive(true);
                if (entry.meshRenderer != null) entry.meshRenderer.enabled = true;
                if (entry.collider != null) entry.collider.enabled = true;
                SetShadowCastersEnabled(entry, entry.shadowsOn);
                if (entry.shadowBlendT >= 0f) ApplyBlendedShadows(entry, chunkSize);
                break;
            case ChunkLOD.Reduced:
                if (!entry.chunkTransform.gameObject.activeSelf)
                    entry.chunkTransform.gameObject.SetActive(true);
                if (entry.meshRenderer != null) entry.meshRenderer.enabled = false;
                if (entry.collider != null) entry.collider.enabled = true;
                SetShadowCastersEnabled(entry, entry.shadowsOn);
                if (entry.shadowBlendT >= 0f) ApplyBlendedShadows(entry, chunkSize);
                break;
            case ChunkLOD.Disabled:
                if (entry.chunkTransform.gameObject.activeSelf)
                    entry.chunkTransform.gameObject.SetActive(false);
                break;
        }
    }

    private void ApplyBlendedShadows(ChunkEntry entry, float chunkSize)
    {
        float minX = entry.gridX * chunkSize, minY = entry.gridY * chunkSize;
        float maxX = (entry.gridX + 1) * chunkSize, maxY = (entry.gridY + 1) * chunkSize;

        if (entry.wallChunks != null)
            for (int i = 0; i < entry.wallChunks.Length; i++)
                if (entry.wallChunks[i] != null)
                    entry.wallChunks[i].SetBlendedShadow(entry.shadowBlendT, minX, minY, maxX, maxY);
    }

    private void RestoreRealShadows(ChunkEntry entry)
    {
        if (entry.wallChunks != null)
            for (int i = 0; i < entry.wallChunks.Length; i++)
                if (entry.wallChunks[i] != null)
                    entry.wallChunks[i].ForceSyncShadow();
    }

    private void SetShadowCastersEnabled(ChunkEntry entry, bool enabled)
    {
        if (entry.shadowCasters == null) return;
        for (int i = 0; i < entry.shadowCasters.Length; i++)
            if (entry.shadowCasters[i] != null)
                entry.shadowCasters[i].enabled = enabled;
    }

    [ContextMenu("Force All Chunks Active")]
    public void ForceAllActive()
    {
        for (int i = 0; i < managedChunks.Count; i++)
        {
            var entry = managedChunks[i];
            if (entry.shadowBlendT >= 0f) RestoreRealShadows(entry);
            entry.shadowsOn = true;
            entry.shadowBlendT = -1f;
            entry.shadowBlendTarget = -1f;
            entry.skylightExposed = true;
            ApplyLOD(ref entry, ChunkLOD.Full, terrain.chunkSize);
            managedChunks[i] = entry;
        }
    }

    [ContextMenu("Refresh Skylight Exposure")]
    public void RefreshSkylightExposureMenu() { RefreshAllSkylightExposure(); }

    public void OnTerrainRegenerated() { Initialize(); }

    // ─── Gizmos ────────────────────────────────────────────────

    private void OnDrawGizmosSelected()
    {
        if (playerTransform != null)
        {
            Gizmos.color = new Color(0, 0.8f, 1f, 0.15f);
            Gizmos.DrawWireSphere(playerTransform.position, playerPhysicsRadius);
        }

        if (targetCamera != null && shadowReachPadding > 0f)
        {
            float halfH = targetCamera.orthographic
                ? targetCamera.orthographicSize
                : Mathf.Tan(targetCamera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 10f;
            float halfW = halfH * targetCamera.aspect;
            Vector3 camPos = targetCamera.transform.position;

            Gizmos.color = new Color(0f, 1f, 0.5f, 0.15f);
            Gizmos.DrawWireCube(camPos, new Vector3((halfW + frustumPadding) * 2f, (halfH + frustumPadding) * 2f, 0.1f));

            Gizmos.color = new Color(1f, 0.5f, 0f, 0.1f);
            Gizmos.DrawWireCube(camPos, new Vector3(
                (halfW + frustumPadding + shadowReachPadding) * 2f,
                (halfH + frustumPadding + shadowReachPadding) * 2f, 0.1f));
        }

        // ── Skylight fan debug rays ──
        if (drawSkylightRays && debugRays != null)
        {
            foreach (var ray in debugRays)
            {
                if (ray.hit)
                {
                    // Green ray with sphere at hit = light reached this surface
                    Gizmos.color = new Color(0.1f, 1f, 0.2f, 0.2f);
                    Gizmos.DrawLine(ray.origin, ray.endpoint);
                    Gizmos.DrawWireSphere(ray.endpoint, 0.4f);
                }
                else
                {
                    // Yellow faint = ray passed through open air (no terrain hit)
                    Gizmos.color = new Color(1f, 1f, 0f, 0.05f);
                    Gizmos.DrawLine(ray.origin, ray.endpoint);
                }
            }
        }

        // ── Per-chunk LOD + skylight coloring ──
        if (managedChunks == null) return;
        foreach (var entry in managedChunks)
        {
            if (!entry.boundsValid) continue;
            if (!entry.skylightExposed)
                Gizmos.color = new Color(0.4f, 0f, 0.6f, 0.12f); // buried = purple
            else switch (entry.currentLOD)
                {
                    case ChunkLOD.Full: Gizmos.color = new Color(0, 1, 0, 0.08f); break;
                    case ChunkLOD.Reduced: Gizmos.color = new Color(1, 1, 0, 0.08f); break;
                    case ChunkLOD.Disabled: Gizmos.color = new Color(1, 0, 0, 0.08f); break;
                }
            Gizmos.DrawCube(entry.worldBounds.center, entry.worldBounds.size);
        }
    }

#if UNITY_EDITOR
    private void OnGUI()
    {
        if (!initialized || !Application.isPlaying) return;
        GUILayout.BeginArea(new Rect(10, 10, 280, 140));
        GUI.color = Color.white;
        GUILayout.Label($"Chunks: {managedChunks.Count} total");
        GUI.color = Color.green;  GUILayout.Label($"  Full: {fullCount}");
        GUI.color = Color.yellow; GUILayout.Label($"  Reduced: {reducedCount}");
        GUI.color = Color.red;    GUILayout.Label($"  Disabled: {disabledCount}");
        GUI.color = new Color(1f, 0.6f, 0.2f);
        GUILayout.Label($"  Shadows: {shadowsActiveCount} active, {blendedShadowCount} blended");
        GUI.color = new Color(0.7f, 0.4f, 1f);
        GUILayout.Label($"  Buried (no skylight): {buriedCount}");
        GUILayout.EndArea();
    }
#endif
}
