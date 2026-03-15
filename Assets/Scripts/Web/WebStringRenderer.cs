using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Manages the visual rendering of a web string.
/// Connects individual WebSegments with a smooth, continuous LineRenderer.
/// This eliminates the "dashed" look and creates a silky, fluid appearance.
/// </summary>
[RequireComponent(typeof(LineRenderer))]
public class WebStringRenderer : MonoBehaviour
{
    [Header("Visual Settings")]
    private float webWidth = 0.2f; // Slightly wider for better overlap
    public Color baseColor = Color.white;
    
    private LineRenderer lineRenderer;
    private List<WebSegment> segments = new List<WebSegment>();
    private List<SpriteRenderer> segmentSprites = new List<SpriteRenderer>();

    void Awake()
    {
        lineRenderer = GetComponent<LineRenderer>();
        lineRenderer.startWidth = webWidth;
        lineRenderer.endWidth = webWidth;
        lineRenderer.useWorldSpace = true;
        
        // Extra smoothness for attachment points
        lineRenderer.numCapVertices = 16;
        lineRenderer.numCornerVertices = 16;

        // Use a lit material so the web respects scene lighting and is hidden in darkness
        Shader litShader = Shader.Find("Universal Render Pipeline/2D/Sprite-Lit-Default");
        if (litShader != null)
        {
            lineRenderer.material = new Material(litShader);
        }
        
        lineRenderer.startColor = baseColor;
        lineRenderer.endColor = baseColor;
    }

    void LateUpdate()
    {
        // Keep tracking the children for positions and colors
        if (transform.childCount != segments.Count)
        {
            RefreshSegments();
        }

        if (segments.Count < 2) 
        {
            lineRenderer.positionCount = 0;
            return;
        }

        lineRenderer.positionCount = segments.Count + 2;
        
        // 1. First point tracks the ACTUAL physical connection point (to close visual gaps)
        Vector2 startPos;
        var first = segments[0];
        if (first.hinge2.enabled && first.hinge2.connectedBody != null)
            startPos = first.hinge2.connectedBody.transform.TransformPoint(first.hinge2.connectedAnchor);
        else
            startPos = first.transform.TransformPoint(first.hinge2.anchor);
            
        lineRenderer.SetPosition(0, startPos);

        // 2. Middle points are the segment centers
        for (int i = 0; i < segments.Count; i++)
        {
            if (segments[i] != null)
            {
                lineRenderer.SetPosition(i + 1, segments[i].transform.position);
            }
        }

        // 3. Last point tracks the ACTUAL physical connection point 
        int lastIdx = segments.Count - 1;
        var last = segments[lastIdx];
        Vector2 endPos;
        if (last.hinge.enabled && last.hinge.connectedBody != null)
            endPos = last.hinge.connectedBody.transform.TransformPoint(last.hinge.connectedAnchor);
        else
            endPos = last.transform.TransformPoint(last.hinge.anchor);

        lineRenderer.SetPosition(segments.Count + 1, endPos);

        // Pulse Synchronization: Inherit the color from the first pulsing segment
        // This ensures the whole strand glows when the spider steps on it.
        if (segmentSprites.Count > 0 && segmentSprites[0] != null)
        {
            lineRenderer.startColor = segmentSprites[0].color;
            lineRenderer.endColor = segmentSprites[0].color;
        }
    }

    private void RefreshSegments()
    {
        segments.Clear();
        segmentSprites.Clear();
        foreach (Transform child in transform)
        {
            WebSegment seg = child.GetComponent<WebSegment>();
            if (seg != null)
            {
                segments.Add(seg);
                var sr = child.GetComponent<SpriteRenderer>();
                segmentSprites.Add(sr);
                
                // Physically the segments still exist for IK and physics,
                // but we hide the individual sprites to show the silky LineRenderer instead.
                if (sr != null) sr.enabled = false;
            }
        }
    }
}
