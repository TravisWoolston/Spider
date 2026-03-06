using System.Collections.Generic;
using UnityEngine;

public static class GlobalAttachmentManager
{
    public static List<GlobalAttachmentPoint> globalAttachmentPoints = new List<GlobalAttachmentPoint>();
    public static float minimumDistanceBetweenPoints = 3f; // Minimum distance between stored global attachment points
    public static void StoreAttachmentPoint(Vector2 point, Vector2 normal, Transform webTransform = null)
    {
        bool tooClose = false;

        foreach (var globalPoint in globalAttachmentPoints)
        {
            if (Vector2.Distance(globalPoint.point, point) < minimumDistanceBetweenPoints)
            {
                tooClose = true;
                break;
            }
        }

        if (!tooClose)
        {
            globalAttachmentPoints.Add(new GlobalAttachmentPoint(point, normal, webTransform));
        }
    }

    public static List<GlobalAttachmentPoint> GetPointsInRange(Vector2 position, float range)
    {
        List<GlobalAttachmentPoint> pointsInRange = new List<GlobalAttachmentPoint>();

        foreach (var point in globalAttachmentPoints)
        {
            if (Vector2.Distance(position, point.point) <= range)
            {
                pointsInRange.Add(point);
            }
        }

        return pointsInRange;
    }

    public static void UpdateWebAttachmentPoints()
    {
        foreach (var globalPoint in globalAttachmentPoints)
        {
            globalPoint.UpdatePosition();
        }
    }

    public static void RemoveStalePoints()
    {
        globalAttachmentPoints.RemoveAll(point => point.webTransform == null);
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
