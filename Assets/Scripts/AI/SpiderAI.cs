using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SpiderAI : MonoBehaviour
{
    public Rigidbody2D target; // The target the spider should move towards
    private SpiderController spiderController;
    private SpiderPathfinding spiderPathfinding; // Reference to the SpiderPathfinding component
    private int currentWaypointIndex = 0;
    public List<Vector2> path = new List<Vector2>();

    public float pathRecalculationInterval = 2f; // Interval between path recalculations

    void Start()
    {
        // Get references to the necessary components
        spiderController = GetComponent<SpiderController>();
        spiderPathfinding = GetComponent<SpiderPathfinding>();

        // Calculate the initial path to the target
        CalculateNewPath();

        // Start the coroutine to recalculate the path periodically
        StartCoroutine(RecalculatePathPeriodically());
    }

    void FixedUpdate()
    {
        if (
            Vector2.Distance(spiderController.head.position, target.transform.position)
            < spiderController.aIFollowDistance
        )
        {
            spiderController.SetInputDirection(new Vector2(0, 0));
            return;
        }
        if (path.Count > 0 && currentWaypointIndex < path.Count)
        {
            Vector2 direction = (
                path[currentWaypointIndex] - (Vector2)spiderController.headRb.transform.position
            ).normalized;

            // Send this direction to the SpiderController to move the spider
            spiderController.SetInputDirection(direction);

            if (
                Vector2.Distance(
                    spiderController.headRb.transform.position,
                    path[currentWaypointIndex]
                ) < spiderController.maxLegExtension
            )
            {
                currentWaypointIndex++;
            }
        }
        else
        {
            // If the path is empty or the spider has reached the end, recalculate the path
            CalculateNewPath();
        }
    }

    void CalculateNewPath()
    {
        if (target.transform != null)
        {
            path = spiderPathfinding.CalculatePath(
                spiderController.headRb.transform.position,
                target.transform.position
            );
            currentWaypointIndex = 0;
        }
    }

    IEnumerator RecalculatePathPeriodically()
    {
        while (true)
        {
            yield return new WaitForSeconds(pathRecalculationInterval);
            CalculateNewPath();
        }
    }

    void OnDrawGizmos()
    {
        if (path != null && path.Count > 0)
        {
            Gizmos.color = Color.blue;
            for (int i = 0; i < path.Count - 1; i++)
            {
                Gizmos.DrawLine(path[i], path[i + 1]);
            }
        }
    }
}
