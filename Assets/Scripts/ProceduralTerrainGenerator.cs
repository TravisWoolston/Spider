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
    public float caveFrequency = 0.15f;
    [Range(0f, 1f)]
    public float caveThreshold = 0.55f;
    public float caveHoleRadius = 0.6f;
    [Tooltip("Separate resolution for cave sampling. Higher = fewer samples = faster.")]
    public float caveResolution = 1.5f;
    [Tooltip("Segments per cave hole. Lower = faster but blockier caves.")]
    [Range(4, 16)]
    public int caveSegments = 6;
    [Tooltip("How much randomness to add to cave hole shapes (0 = perfect circles, 1 = very jagged)")]
    [Range(0f, 1f)]
    public float caveIrregularity = 0.4f;
    [Tooltip("Secondary noise frequency for cave shape distortion")]
    public float caveShapeFrequency = 0.5f;

    [Header("Randomization")]
    public float seed = 0f;

    [Header("Performance")]
    [Tooltip("Maximum number of cave holes to generate")]
    public int maxCaveHoles = 500;
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


        // 3. Cut Caves - ORGANIC SHAPES
        if (generateCaves)
        {
            float caveSampleRes = Mathf.Max(caveResolution, resolution);
            int cols = Mathf.CeilToInt(width / caveSampleRes);
            int rows = Mathf.CeilToInt(height / caveSampleRes);

            int holeCount = 0;
            float shapeSeedOffset = seed + 1000f;

            for (int x = 0; x < cols && holeCount < maxCaveHoles; x++)
            {
                float worldX = x * caveSampleRes;

                for (int y = 0; y < rows && holeCount < maxCaveHoles; y++)
                {
                    float worldY = -y * caveSampleRes;

                    float noise = Mathf.PerlinNoise(
                        worldX * caveFrequency + seed,
                        worldY * caveFrequency + seed);

                    if (noise > caveThreshold)
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
                            float variedRadius = caveHoleRadius * radiusVariation;

                            float sizeVariation = (noise - caveThreshold) / (1f - caveThreshold);
                            variedRadius *= Mathf.Lerp(0.7f, 1.3f, sizeVariation);

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
}
