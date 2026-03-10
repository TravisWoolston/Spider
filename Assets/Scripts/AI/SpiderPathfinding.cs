using System.Collections.Generic;
using UnityEngine;

public class SpiderPathfinding : MonoBehaviour
{
    public SpiderController spiderController;

    // Heuristic function to estimate the distance to the target
    private float Heuristic(Vector2 from, Vector2 to)
    {
        return Vector2.Distance(from, to);
    }

    // Simplified A* pathfinding tailored for the spider's movement using global attachment points
public int maxIterations = 500; // Set a reasonable limit
public List<Vector2> CalculatePath(Vector2 startPosition, Vector2 targetPosition)
{
    List<Node> openList = new List<Node>();
    HashSet<Node> closedList = new HashSet<Node>();

    Node startNode = new Node(startPosition, null, 0, Heuristic(startPosition, targetPosition));
    openList.Add(startNode);

    int iterations = 0;
    while (openList.Count > 0)
    {
        iterations++;
        if (iterations > maxIterations) // Stop if we've exceeded the maximum iterations
        {
            Debug.Log("Pathfinding terminated due to exceeding max iterations");
            return new List<Vector2>(); // Return empty path
        }

        // Sort and get the node with the lowest F score
        openList.Sort((nodeA, nodeB) => nodeA.F.CompareTo(nodeB.F));
        Node currentNode = openList[0];

        // If we are close enough to the target, return the path
        if (Vector2.Distance(currentNode.Position, targetPosition) <= spiderController.maxLegExtension)
        {
            return ReconstructPath(currentNode);
        }

        openList.Remove(currentNode);
        closedList.Add(currentNode);

        List<GlobalAttachmentPoint> neighbors = GlobalAttachmentManager.GetPointsInRange(currentNode.Position, spiderController.maxLegExtension);
        foreach (var neighbor in neighbors)
        {
            Node neighborNode = new Node(neighbor.point, currentNode, 0, Heuristic(neighbor.point, targetPosition));

            if (closedList.Contains(neighborNode))
                continue;

            float tentativeG = currentNode.G + Vector2.Distance(currentNode.Position, neighborNode.Position);

            if (openList.Contains(neighborNode) && tentativeG >= neighborNode.G)
                continue;

            neighborNode.Parent = currentNode;
            neighborNode.G = tentativeG;
            neighborNode.H = Heuristic(neighborNode.Position, targetPosition);

            if (!openList.Contains(neighborNode))
                openList.Add(neighborNode);
        }
    }

    // Debug.Log("Pathfinding failed to find a path");
    return new List<Vector2>(); // Return an empty path if no valid path is found
}


    // Reconstruct the path from the end node to the start node
    private List<Vector2> ReconstructPath(Node endNode)
    {
        List<Vector2> path = new List<Vector2>();
        Node currentNode = endNode;

        while (currentNode != null)
        {
            path.Add(currentNode.Position);
            currentNode = currentNode.Parent;
        }

        path.Reverse();
        return path;
    }

    // Node class representing each point in the path
    private class Node
    {
        public Vector2 Position;
        public Node Parent;
        public float G;  // Cost from start node
        public float H;  // Heuristic cost to target node
        public float F => G + H;  // Total cost

        public Node(Vector2 position, Node parent, float g, float h)
        {
            Position = position;
            Parent = parent;
            G = g;
            H = h;
        }

        public override bool Equals(object obj)
        {
            if (obj == null || GetType() != obj.GetType()) return false;
            Node other = (Node)obj;
            return Position.Equals(other.Position);
        }

        public override int GetHashCode()
        {
            return Position.GetHashCode();
        }
    }
}
