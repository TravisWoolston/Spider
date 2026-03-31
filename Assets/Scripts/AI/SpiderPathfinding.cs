using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Optimized A* Pathfinding for the SpiderControllerIK.
/// Uses GlobalAttachmentManager to navigate between web strands and surfaces.
/// </summary>
public class SpiderPathfinding : MonoBehaviour
{
    private SpiderControllerIK spiderController;

    [Header("A* Settings")]
    [Tooltip("Maximum nodes to evaluate before giving up on a path.")]
    public int maxIterations = 500;
    [Tooltip("Movement cost multiplier for web strands. Values < 1 make the AI prefer webs.")]
    public float webCostMultiplier = 0.5f;

    private void Awake()
    {
        // Auto-detect the IK controller on the same GameObject
        spiderController = GetComponent<SpiderControllerIK>();
    }

    /// <summary>
    /// Calculates a path of attachment points from start to target.
    /// </summary>
    public List<Vector2> CalculatePath(Vector2 startPosition, Vector2 targetPosition)
    {
        if (spiderController == null)
        {
            Debug.LogError("[SpiderPathfinding] No SpiderControllerIK found on " + gameObject.name);
            return new List<Vector2>();
        }

        // 1. Initialize A* structures
        List<Node> openList = new List<Node>();
        HashSet<Vector2> closedSet = new HashSet<Vector2>();

        // We use a slight reach buffer to ensure the spider can actually make the strides
        float reach = spiderController.legExtensionMax * 0.9f;

        Node startNode = new Node(startPosition, null, 0, Vector2.Distance(startPosition, targetPosition));
        openList.Add(startNode);

        int iterations = 0;
        while (openList.Count > 0)
        {
            iterations++;
            if (iterations > maxIterations) break;

            // 2. Efficiently pick the node with the lowest F cost
            // Optimization: Single pass min search is faster than Sort() for typical path lengths
            int bestIdx = 0;
            for (int i = 1; i < openList.Count; i++)
            {
                if (openList[i].F < openList[bestIdx].F) bestIdx = i;
            }

            Node currentNode = openList[bestIdx];

            // 3. Goal Check
            if (Vector2.Distance(currentNode.Position, targetPosition) <= reach)
            {
                List<Vector2> path = ReconstructPath(currentNode);
                path.Add(targetPosition); 
                return path;
            }

            openList.RemoveAt(bestIdx);
            closedSet.Add(currentNode.Position);

            // 4. Neighbor Expansion using GlobalAttachmentManager
            List<GlobalAttachmentPoint> pointsNear = GlobalAttachmentManager.GetPointsInRange(currentNode.Position, reach);
            
            foreach (var point in pointsNear)
            {
                if (closedSet.Contains(point.Position)) continue;

                float stepDist = Vector2.Distance(currentNode.Position, point.Position);
                if (point.webTransform != null) stepDist *= webCostMultiplier;
                
                float gCost = currentNode.G + stepDist;
                float hCost = Vector2.Distance(point.Position, targetPosition);

                // Check if we already have this point in open list
                Node existingNode = null;
                foreach(var n in openList)
                {
                    if (n.Position == point.Position)
                    {
                        existingNode = n;
                        break;
                    }
                }

                if (existingNode != null)
                {
                    if (gCost < existingNode.G)
                    {
                        existingNode.Parent = currentNode;
                        existingNode.G = gCost;
                    }
                }
                else
                {
                    openList.Add(new Node(point.Position, currentNode, gCost, hCost));
                }
            }
        }

        return new List<Vector2>();
    }

    private List<Vector2> ReconstructPath(Node endNode)
    {
        List<Vector2> path = new List<Vector2>();
        Node curr = endNode;
        while (curr != null)
        {
            path.Add(curr.Position);
            curr = curr.Parent;
        }
        path.Reverse();
        return path;
    }

    private class Node
    {
        public Vector2 Position;
        public Node Parent;
        public float G;
        public float H;
        public float F => G + H;

        public Node(Vector2 pos, Node parent, float g, float h)
        {
            Position = pos;
            Parent = parent;
            G = g;
            H = h;
        }
    }
}
