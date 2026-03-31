using System.Collections.Generic;
using UnityEngine;

public static class GlobalAttachmentManager
{
    public static List<GlobalAttachmentPoint> staticPoints = new List<GlobalAttachmentPoint>();
    public static List<GlobalAttachmentPoint> dynamicPoints = new List<GlobalAttachmentPoint>();
    
    // Spatial Partitioning
    private static float cellSize = 5f;
    private static Dictionary<Vector2Int, List<GlobalAttachmentPoint>> staticGrid = new Dictionary<Vector2Int, List<GlobalAttachmentPoint>>();
    private static Dictionary<Vector2Int, List<GlobalAttachmentPoint>> dynamicGrid = new Dictionary<Vector2Int, List<GlobalAttachmentPoint>>();
    
    public static float minimumDistanceBetweenPoints = 1.0f; 

    public static IEnumerable<GlobalAttachmentPoint> AllPoints()
    {
        foreach (var p in staticPoints) yield return p;
        foreach (var p in dynamicPoints) yield return p;
    }

    public static int TotalPoints => staticPoints.Count + dynamicPoints.Count;

    private static Vector2Int WorldToGrid(Vector2 pos)
    {
        return new Vector2Int(Mathf.FloorToInt(pos.x / cellSize), Mathf.FloorToInt(pos.y / cellSize));
    }

    public static void StoreAttachmentPoint(Vector2 point, Vector2 normal, Transform webTransform = null, float? overrideMinDist = null)
    {
        bool isDynamic = webTransform != null;
        var activeGrid = isDynamic ? dynamicGrid : staticGrid;
        var activeList = isDynamic ? dynamicPoints : staticPoints;
        
        Vector2Int gridPos = WorldToGrid(point);
        float minDist = overrideMinDist ?? minimumDistanceBetweenPoints;
        float minDistSqr = minDist * minDist;
        
        // Deduplication check
        for (int x = -1; x <= 1; x++)
        {
            for (int y = -1; y <= 1; y++)
            {
                Vector2Int cell = gridPos + new Vector2Int(x, y);
                if (activeGrid.TryGetValue(cell, out var pointsInCell))
                {
                    foreach (var gap in pointsInCell)
                    {
                        if (Vector2.SqrMagnitude(gap.Position - point) < minDistSqr)
                        {
                            return; // Too close
                        }
                    }
                }
            }
        }

        // Add new point
        var newPoint = new GlobalAttachmentPoint(point, normal, webTransform);
        activeList.Add(newPoint);
        
        if (!activeGrid.ContainsKey(gridPos)) activeGrid[gridPos] = new List<GlobalAttachmentPoint>();
        activeGrid[gridPos].Add(newPoint);
    }

    public static List<GlobalAttachmentPoint> GetPointsInRange(Vector2 position, float range)
    {
        List<GlobalAttachmentPoint> results = new List<GlobalAttachmentPoint>();
        Vector2Int min = WorldToGrid(position - new Vector2(range, range));
        Vector2Int max = WorldToGrid(position + new Vector2(range, range));
        float rangeSqr = range * range;

        for (int x = min.x; x <= max.x; x++)
        {
            for (int y = min.y; y <= max.y; y++)
            {
                Vector2Int cell = new Vector2Int(x, y);
                // Check Static Grid
                if (staticGrid.TryGetValue(cell, out var sPoints))
                {
                    foreach (var gap in sPoints)
                        if (Vector2.SqrMagnitude(gap.Position - position) <= rangeSqr) results.Add(gap);
                }
                // Check Dynamic Grid
                if (dynamicGrid.TryGetValue(cell, out var dPoints))
                {
                    foreach (var gap in dPoints)
                        if (Vector2.SqrMagnitude(gap.Position - position) <= rangeSqr) results.Add(gap);
                }
            }
        }

        return results;
    }

    public static void UpdateWebAttachmentPoints()
    {
        // ONLY rebuild the dynamic grid. Static grid remains cached.
        dynamicGrid.Clear();
        foreach (var gap in dynamicPoints)
        {
            if (gap.webTransform == null) continue; 
            
            Vector2 worldPos = gap.Position;
            Vector2Int gridPos = WorldToGrid(worldPos);
            if (!dynamicGrid.ContainsKey(gridPos)) dynamicGrid[gridPos] = new List<GlobalAttachmentPoint>();
            dynamicGrid[gridPos].Add(gap);
        }
    }

    public static void RemoveStalePoints()
    {
        dynamicPoints.RemoveAll(point => point.webTransform == null);
        UpdateWebAttachmentPoints(); 
    }

    public static void ClearGlobalPoints()
    {
        staticPoints.Clear();
        dynamicPoints.Clear();
        staticGrid.Clear();
        dynamicGrid.Clear();
    }

    /// <summary>
    /// Scans the entire scene for Default layer surfaces and caches them.
    /// This prevents performance spikes during gameplay as AI discovers terrain.
    /// </summary>
    public static void CacheSceneTerrain(LayerMask terrainLayer, float density)
    {
        // Clear static data before re-caching
        staticPoints.Clear();
        staticGrid.Clear();

        Collider2D[] allTerrain = Object.FindObjectsByType<Collider2D>(FindObjectsSortMode.None);
        List<Collider2D> filtered = new List<Collider2D>();
        foreach (var col in allTerrain)
        {
            if (((1 << col.gameObject.layer) & terrainLayer) != 0)
                filtered.Add(col);
        }

        Debug.Log($"[GlobalAttachmentManager] Caching surfaces for {filtered.Count} colliders (density {density})...");

        foreach (var col in filtered)
        {
            if (col is BoxCollider2D box) SampleBox(box, density);
            else if (col is CircleCollider2D circle) SampleCircle(circle, density);
            else if (col is PolygonCollider2D poly) SamplePolygon(poly, density);
            else if (col is EdgeCollider2D edge) SampleEdge(edge, density);
            else if (col is CompositeCollider2D composite) SampleComposite(composite, density);
            else SampleFallback(col, density);
        }

        Debug.Log($"[GlobalAttachmentManager] Caching complete. Total static points: {staticPoints.Count}");
    }

    private static void SampleBox(BoxCollider2D box, float density)
    {
        Vector2 size = box.size;
        Vector2 center = box.offset;
        
        // Sample 4 edges
        SampleLine(box.transform.TransformPoint(center + new Vector2(-size.x/2, -size.y/2)), box.transform.TransformPoint(center + new Vector2(size.x/2, -size.y/2)), density, box.transform.up);
        SampleLine(box.transform.TransformPoint(center + new Vector2(size.x/2, -size.y/2)), box.transform.TransformPoint(center + new Vector2(size.x/2, size.y/2)), density, box.transform.right);
        SampleLine(box.transform.TransformPoint(center + new Vector2(size.x/2, size.y/2)), box.transform.TransformPoint(center + new Vector2(-size.x/2, size.y/2)), density, -box.transform.up);
        SampleLine(box.transform.TransformPoint(center + new Vector2(-size.x/2, size.y/2)), box.transform.TransformPoint(center + new Vector2(-size.x/2, -size.y/2)), density, -box.transform.right);
    }

    private static void SampleCircle(CircleCollider2D circle, float density)
    {
        float circumference = 2 * Mathf.PI * circle.radius * circle.transform.lossyScale.magnitude;
        int count = Mathf.Max(4, Mathf.CeilToInt(circumference / density));
        float angleStep = 360f / count;
        
        for (int i = 0; i < count; i++)
        {
            float rad = i * angleStep * Mathf.Deg2Rad;
            Vector2 dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
            Vector2 localPos = circle.offset + dir * circle.radius;
            Vector2 worldPos = circle.transform.TransformPoint(localPos);
            Vector2 worldNormal = circle.transform.TransformDirection(dir);
            StoreAttachmentPoint(worldPos, worldNormal, null);
        }
    }

    private static void SamplePolygon(PolygonCollider2D poly, float density)
    {
        for (int p = 0; p < poly.pathCount; p++)
        {
            Vector2[] path = poly.GetPath(p);
            for (int i = 0; i < path.Length; i++)
            {
                Vector2 start = poly.transform.TransformPoint(path[i] + poly.offset);
                Vector2 end = poly.transform.TransformPoint(path[(i + 1) % path.Length] + poly.offset);
                SampleLine(start, end, density, (end - start).normalized); // Normal is tricky here, but StoreAttachmentPoint handles close-enough
            }
        }
    }

    private static void SampleEdge(EdgeCollider2D edge, float density)
    {
        for (int i = 0; i < edge.pointCount - 1; i++)
        {
            Vector2 start = edge.transform.TransformPoint(edge.points[i] + edge.offset);
            Vector2 end = edge.transform.TransformPoint(edge.points[i+1] + edge.offset);
            SampleLine(start, end, density, Vector2.up); 
        }
    }

    private static void SampleComposite(CompositeCollider2D comp, float density)
    {
        for (int p = 0; p < comp.pathCount; p++)
        {
            Vector2[] path = new Vector2[comp.GetPathPointCount(p)];
            comp.GetPath(p, path);
            for (int i = 0; i < path.Length; i++)
            {
                Vector2 start = comp.transform.TransformPoint(path[i] + comp.offset);
                Vector2 end = comp.transform.TransformPoint(path[(i + 1) % path.Length] + comp.offset);
                SampleLine(start, end, density, Vector2.up);
            }
        }
    }

    private static void SampleFallback(Collider2D col, float density)
    {
        // For mystery colliders, just do a very coarse grid of ClosestPoint
        Bounds b = col.bounds;
        float coarseStep = density * 2f;
        for (float x = b.min.x; x <= b.max.x; x += coarseStep)
        {
            for (float y = b.min.y; y <= b.max.y; y += coarseStep)
            {
                Vector2 p = col.ClosestPoint(new Vector2(x, y));
                StoreAttachmentPoint(p, Vector2.up, null);
            }
        }
    }

    private static void SampleLine(Vector2 start, Vector2 end, float density, Vector2 fallbackNormal)
    {
        float dist = Vector2.Distance(start, end);
        int steps = Mathf.Max(1, Mathf.RoundToInt(dist / density));
        for (int i = 0; i <= steps; i++)
        {
            Vector2 p = Vector2.Lerp(start, end, (float)i / steps);
            StoreAttachmentPoint(p, fallbackNormal, null);
        }
    }
}


public class GlobalAttachmentPoint
{
    private Vector2 localPoint;
    public Vector2 normal;
    public Transform webTransform; 

    public Vector2 Position 
    {
        get 
        {
            if (webTransform != null) return webTransform.TransformPoint(localPoint);
            return localPoint;
        }
    }

    public GlobalAttachmentPoint(Vector2 worldPoint, Vector2 normal, Transform webTransform = null)
    {
        this.webTransform = webTransform;
        this.normal = normal;

        if (webTransform != null)
            this.localPoint = webTransform.InverseTransformPoint(worldPoint);
        else
            this.localPoint = worldPoint;
    }
}


// public class AttachmentPoint
// {
//     public Vector2 point;
//     public Vector2 normal;

//     public AttachmentPoint(Vector2 point, Vector2 normal)
//     {
//         this.point = point;
//         this.normal = normal;
//     }
// }
