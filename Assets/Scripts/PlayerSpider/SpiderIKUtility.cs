using UnityEngine;

public static class SpiderIKUtility
{
    /// <summary>
    /// Solves 2D 2-Bone IK using the Law of Cosines.
    /// </summary>
    /// <param name="localTarget">Target position relative to the root joint.</param>
    /// <param name="a">Length of the first segment (Thigh).</param>
    /// <param name="b">Length of the second segment (Shin).</param>
    /// <param name="isLeftSide">Whether to flip the knee direction.</param>
    /// <returns>A Vector2 containing (Thigh Angle, Knee Angle) in degrees.</returns>
    public static Vector2 Solve2BIK(Vector2 localTarget, float a, float b, bool isLeftSide)
    {
        float d2 = localTarget.sqrMagnitude;
        float d = Mathf.Sqrt(d2);

        // Clamp distance to reach (prevent NaN)
        float maxReach = a + b;
        float minReach = Mathf.Abs(a - b);
        d = Mathf.Clamp(d, minReach + 0.01f, maxReach - 0.01f);
        d2 = d * d;

        // Law of Cosines for interior angles
        float cosAlpha = (a * a + d2 - b * b) / (2 * a * d);
        float alpha = Mathf.Acos(Mathf.Clamp(cosAlpha, -1f, 1f));

        float cosBeta = (a * a + b * b - d2) / (2 * a * b);
        float beta = Mathf.Acos(Mathf.Clamp(cosBeta, -1f, 1f));

        // Angle from origin to target
        float angleToTarget = Mathf.Atan2(localTarget.y, localTarget.x);

        float thighAngle;
        float kneeAngle;

        if (isLeftSide)
        {
            thighAngle = (angleToTarget + alpha) * Mathf.Rad2Deg;
            kneeAngle = (Mathf.PI - beta) * Mathf.Rad2Deg;
        }
        else
        {
            thighAngle = (angleToTarget - alpha) * Mathf.Rad2Deg;
            kneeAngle = (beta - Mathf.PI) * Mathf.Rad2Deg;
        }

        return new Vector2(thighAngle, kneeAngle);
    }
}
