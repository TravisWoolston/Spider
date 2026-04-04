using UnityEngine;
using Clipper2Lib;
using System.Collections.Generic;


[RequireComponent(typeof(DestructibleTerrain))]
public class ProceduralTerrainGenerator : MonoBehaviour
{
    [Header("Chunk Size")]
    public float width = 20f;
    public float height = 20f;
    [Tooltip("How detailed the noise sampling is. Lower numbers = finer detail but slower generation.")]
    public float resolution = 0.5f;

    [Header("Surface Generation")]
    public bool generateSurface = true;
    public float surfaceDepth = 2f;
    public float surfaceFrequency = 0.1f;
    public float surfaceAmplitude = 2f;

    [Header("Cave Generation")]
    public bool generateCaves = true;
    [Tooltip("Small scale frequency for wall detail")]
    public float caveFrequency = 0.08f;
    [Tooltip("Large scale frequency for overall cavern shapes")]
    public float cavernFrequency = 0.03f;
    [Range(0f, 1f)]
    public float caveThreshold = 0.5f;
    public float caveHoleRadius = 1.2f;
    [Tooltip("Denser sampling creates smoother, merged caverns. Keep between 0.5 and 1.5.")]
    public float caveResolution = 0.8f;

    [Header("Cave Depth Scaling")]
    [Tooltip("World Y depth where caves start to get larger/more frequent")]
    public float depthStart = 0f;
    [Tooltip("World Y depth where caves reach maximum size/frequency")]
    public float depthEnd = -40f;
    [Tooltip("How much to lower the threshold (more caves) at max depth")]
    [Range(0f, 1f)]
    public float deeperCaveThresholdBoost = 0.2f;
    [Tooltip("How much to increase the radius (larger caves) at max depth")]
    public float deeperCaveRadiusBoost = 2.0f;
    [Tooltip("Controls how sparse cave systems are. Higher = larger solid gaps between systems.")]
    [Range(0f, 1f)]
    public float cavernSystemThreshold = 0.5f;

    [Header("Cave Details")]
    [Tooltip("Segments per cave hole. 8-12 is good for organic shapes.")]
    [Range(4, 16)]
    public int caveSegments = 10;
    [Tooltip("How much randomness to add to cave hole shapes")]
    [Range(0f, 1f)]
    public float caveIrregularity = 0.5f;
    public float caveShapeFrequency = 0.5f;

    [Header("Randomization")]
    public float seed = 0f;

    [Header("Performance")]
    [Tooltip("Maximum number of cave holes to generate (increase for large maps)")]
    public int maxCaveHoles = 5000;
    [Tooltip("Simplify paths after boolean operations to reduce vertex count")]
    public float simplifyTolerance = 0.05f;
    [Tooltip("Maximum paths in the final collider. Excess islands will be culled.")]
    public int maxColliderPaths = 100;
    [Tooltip("Minimum area for a path to be kept (removes tiny debris)")]
    public float minPathArea = 0.5f;

    [ContextMenu("Generate Terrain")]
    public void Generate()
    {
        // 1. Create Base Solid Block
        PathD baseBlock = new PathD(4);
        baseBlock.Add(new PointD(0, 0));
        baseBlock.Add(new PointD(width, 0));
        baseBlock.Add(new PointD(width, -height));
        baseBlock.Add(new PointD(0, -height));

        PathsD subjectPaths = new PathsD() { baseBlock };
        PathsD clipPaths = new PathsD();

        // 2. Cut Surface & store surface profile for shadow caster
        Vector2[] surfacePoints = null;
        if (generateSurface)
        {
            int steps = Mathf.CeilToInt(width / resolution);
            PathD surfaceClip = new PathD(steps + 4);

            surfaceClip.Add(new PointD(-1f, height + 1f));
            surfaceClip.Add(new PointD(width + 1f, height + 1f));

            // Store surface points left-to-right for shadow caster
            surfacePoints = new Vector2[steps + 1];

            for (int i = steps; i >= 0; i--)
            {
                float x = Mathf.Clamp(i * resolution, 0f, width);
                float y = -surfaceDepth + Mathf.PerlinNoise(x * surfaceFrequency + seed, seed) * surfaceAmplitude;
                surfaceClip.Add(new PointD(x, y));
                surfacePoints[steps - i] = new Vector2(x, y); // left-to-right order
            }
            clipPaths.Add(surfaceClip);
        }


        // 3. Cut Caves - CAVERN LAYERED NOISE
        if (generateCaves)
        {
            float caveSampleRes = Mathf.Max(caveResolution, 0.1f);
            int cols = Mathf.CeilToInt(width / caveSampleRes);
            int rows = Mathf.CeilToInt(height / caveSampleRes);

            int holeCount = 0;
            float shapeSeedOffset = seed + 1000f;

            for (int x = 0; x < cols; x++)
            {
                if (holeCount >= maxCaveHoles)
                {
                    Debug.LogWarning($"[ProceduralTerrain] Max cave holes ({maxCaveHoles}) reached! Remaining terrain will be solid. Increase 'Max Cave Holes' in the inspector for larger maps.");
                    break;
                }

                float worldX = x * caveSampleRes;

                for (int y = 0; y < rows; y++)
                {
                    if (holeCount >= maxCaveHoles) break;

                    float worldY = -y * caveSampleRes;

                    // 3a. Depth-Based Scaling
                    // Tunnels become larger and more frequent the deeper they go.
                    float depthFactor = Mathf.InverseLerp(depthStart, depthEnd, worldY);
                    float currentThreshold = caveThreshold - (deeperCaveThresholdBoost * depthFactor);
                    float currentRadius = caveHoleRadius + (deeperCaveRadiusBoost * depthFactor);

                    // Layer 1: Base Cavern Pockets (Small Detail)
                    float noise = Mathf.PerlinNoise(
                        worldX * caveFrequency + seed,
                        worldY * caveFrequency + seed);

                    // Layer 2: Massive Open Areas (Large Scale 'System Mask')
                    float macroCavern = Mathf.PerlinNoise(
                        worldX * cavernFrequency + seed + 500f,
                        worldY * cavernFrequency + seed + 500f);

                    // SYSTEM MASK: Only allow caves where macro-noise is high.
                    // This creates 'islands' of caves rather than an even field.
                    if (macroCavern < cavernSystemThreshold) continue;

                    if (noise > currentThreshold)
                    {
                        PathD hole = new PathD(caveSegments);

                        for (int i = caveSegments - 1; i >= 0; i--)
                        {
                            float angle = (i * Mathf.PI * 2f) / caveSegments;

                            float angleNoise = Mathf.PerlinNoise(
                                worldX * caveShapeFrequency + Mathf.Cos(angle) * 2f + shapeSeedOffset,
                                worldY * caveShapeFrequency + Mathf.Sin(angle) * 2f + shapeSeedOffset
                            );

                            float radiusVariation = Mathf.Lerp(1f, 0.6f + angleNoise * 0.8f, caveIrregularity);
                            float variedRadius = currentRadius * radiusVariation;

                            // Scale hole size based on noise intensity relative to threshold
                            float sizeVariation = (noise - currentThreshold) / (1f - currentThreshold);
                            variedRadius *= Mathf.Lerp(0.8f, 1.5f, Mathf.Max(0f, sizeVariation));

                            hole.Add(new PointD(
                                worldX + Mathf.Cos(angle) * variedRadius,
                                worldY + Mathf.Sin(angle) * variedRadius));
                        }
                        clipPaths.Add(hole);
                        holeCount++;
                    }
                }
            }

            Debug.Log($"[ProceduralTerrain] Generated {holeCount} cave holes");
        }

        // 4. Union all clip paths
        clipPaths = Clipper.Union(clipPaths, new PathsD(), FillRule.NonZero, 2);

        // 5. Perform Boolean Difference
        PathsD solution = Clipper.Difference(subjectPaths, clipPaths, FillRule.NonZero, 2);

        // 6. Simplify paths to reduce vertex count
        if (simplifyTolerance > 0f)
        {
            solution = Clipper.SimplifyPaths(solution, simplifyTolerance);
        }

        // 7. Safety check: limit vertex count
        int totalVertices = 0;
        foreach (var path in solution)
            totalVertices += path.Count;

        Debug.Log($"[ProceduralTerrain] Result: {solution.Count} paths, {totalVertices} total vertices");

        if (totalVertices > 20000)
        {
            Debug.LogWarning($"[ProceduralTerrain] Too many vertices ({totalVertices})! Increasing simplification...");
            solution = Clipper.SimplifyPaths(solution, simplifyTolerance * 3f);

            totalVertices = 0;
            foreach (var path in solution)
                totalVertices += path.Count;
            Debug.Log($"[ProceduralTerrain] After aggressive simplification: {totalVertices} vertices");
        }

        // 8. Remove tiny islands and limit path count for physics performance
        if (solution.Count > maxColliderPaths || minPathArea > 0f)
        {
            var filteredPaths = new List<PathD>();

            foreach (var path in solution)
            {
                double area = Mathf.Abs((float)Clipper.Area(path));
                if (area >= minPathArea)
                    filteredPaths.Add(path);
            }

            // Sort by area (largest first) and keep only the biggest
            filteredPaths.Sort((a, b) =>
                Mathf.Abs((float)Clipper.Area(b)).CompareTo(Mathf.Abs((float)Clipper.Area(a))));

            int keepCount = Mathf.Min(filteredPaths.Count, maxColliderPaths);
            solution = new PathsD();
            for (int i = 0; i < keepCount; i++)
                solution.Add(filteredPaths[i]);

            Debug.Log($"[ProceduralTerrain] Filtered to {solution.Count} paths (removed tiny islands)");
        }

        // 9. Force Visuals to Update on Manager (Manager handles chunks)
        DestructibleTerrain dt = GetComponent<DestructibleTerrain>();
        if (dt != null)
        {
            dt.SetGeneratorResults(solution);
        }
    }

    private void OnDrawGizmosSelected()
    {
        // Draw Depth Indicators
        Vector3 pos = transform.position;
        
        Gizmos.color = new Color(0, 1, 1, 0.5f); // Cyan for Start
        Gizmos.DrawLine(pos + new Vector3(0, depthStart, 0), pos + new Vector3(width, depthStart, 0));
        
        Gizmos.color = new Color(1, 0.5f, 0, 0.5f); // Orange for End
        Gizmos.DrawLine(pos + new Vector3(0, depthEnd, 0), pos + new Vector3(width, depthEnd, 0));

        // Draw boundaries
        Gizmos.color = Color.white;
        Gizmos.DrawWireCube(pos + new Vector3(width * 0.5f, -height * 0.5f, 0), new Vector3(width, height, 0.1f));
    }
}
