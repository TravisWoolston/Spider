using System.Collections.Generic;
using UnityEngine;

public static class GlobalAttachmentManager
{
    public static List<GlobalAttachmentPoint> globalAttachmentPoints = new List<GlobalAttachmentPoint>();
    
    // Spatial Partitioning
    private static float cellSize = 5f;
    private static Dictionary<Vector2Int, List<GlobalAttachmentPoint>> grid = new Dictionary<Vector2Int, List<GlobalAttachmentPoint>>();
    
    public static float minimumDistanceBetweenPoints = 3f; 

    private static Vector2Int WorldToGrid(Vector2 pos)
    {
        return new Vector2Int(Mathf.FloorToInt(pos.x / cellSize), Mathf.FloorToInt(pos.y / cellSize));
    }

    public static void StoreAttachmentPoint(Vector2 point, Vector2 normal, Transform webTransform = null)
    {
        Vector2Int gridPos = WorldToGrid(point);
        
        // Check current and neighboring cells only (3x3 grid)
        for (int x = -1; x <= 1; x++)
        {
            for (int y = -1; y <= 1; y++)
            {
                Vector2Int cell = gridPos + new Vector2Int(x, y);
                if (grid.TryGetValue(cell, out var pointsInCell))
                {
                    foreach (var gap in pointsInCell)
                    {
                        if (Vector2.SqrMagnitude(gap.point - point) < minimumDistanceBetweenPoints * minimumDistanceBetweenPoints)
                        {
                            return; // Too close
                        }
                    }
                }
            }
        }

        // Add new point
        var newPoint = new GlobalAttachmentPoint(point, normal, webTransform);
        globalAttachmentPoints.Add(newPoint);
        
        if (!grid.ContainsKey(gridPos)) grid[gridPos] = new List<GlobalAttachmentPoint>();
        grid[gridPos].Add(newPoint);
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
                if (grid.TryGetValue(cell, out var pointsInCell))
                {
                    foreach (var gap in pointsInCell)
                    {
                        if (Vector2.SqrMagnitude(gap.point - position) <= rangeSqr)
                        {
                            results.Add(gap);
                        }
                    }
                }
            }
        }

        return results;
    }

    public static void UpdateWebAttachmentPoints()
    {
        // Clear grid and rebuild as positions change
        grid.Clear();
        foreach (var gap in globalAttachmentPoints)
        {
            gap.UpdatePosition();
            Vector2Int gridPos = WorldToGrid(gap.point);
            if (!grid.ContainsKey(gridPos)) grid[gridPos] = new List<GlobalAttachmentPoint>();
            grid[gridPos].Add(gap);
        }
    }

    public static void RemoveStalePoints()
    {
        globalAttachmentPoints.RemoveAll(point => point.webTransform == null);
        UpdateWebAttachmentPoints(); // Rebuild grid
    }
}


public class GlobalAttachmentPoint
{
    public Vector2 point;
    public Vector2 normal;
    public Transform webTransform; // Reference to the web's Transform

    public GlobalAttachmentPoint(Vector2 point, Vector2 normal, Transform webTransform = null)
    {
        this.point = point;
        this.normal = normal;
        this.webTransform = webTransform;
    }

    public void UpdatePosition()
    {
        if (webTransform != null)
        {
            // Assuming the point is a local position on the web
            this.point = webTransform.TransformPoint(this.point);
        }
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
