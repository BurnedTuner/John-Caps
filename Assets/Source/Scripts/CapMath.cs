using UnityEngine;

/// <summary>
/// Math helpers for the cap simulation. All functions operate on the XZ plane.
/// </summary>
public static class CapMath
{
    public static Vector2 VerticalImpactDirection(Vector2 impactCentre, Vector2 capCentre, Vector2 centredFallback)
    {
        Vector2 away = capCentre - impactCentre;
        if (away.sqrMagnitude > 0.000001f) return away.normalized;
        if (centredFallback.sqrMagnitude > 0.000001f) return centredFallback.normalized;
        float angle = Random.value * Mathf.PI * 2f;
        return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
    }

    public static bool TrySweepCircle(
        Vector2 start, Vector2 displacement, Vector2 targetCentre, float combinedRadius, out float fraction)
    {
        fraction = 0f;
        Vector2 offset = start - targetCentre;
        float radiusSquared = combinedRadius * combinedRadius;

        if (offset.sqrMagnitude <= radiusSquared)
        {
            fraction = 0f;
            return true;
        }

        float a = Vector2.Dot(displacement, displacement);
        if (a <= 0.0000001f) return false;

        float b = 2f * Vector2.Dot(offset, displacement);
        float c = Vector2.Dot(offset, offset) - radiusSquared;
        float discriminant = b * b - 4f * a * c;
        if (discriminant < 0f) return false;

        float root = Mathf.Sqrt(discriminant);
        float first = (-b - root) / (2f * a);
        if (first >= 0f && first <= 1f) { fraction = first; return true; }

        float second = (-b + root) / (2f * a);
        if (second >= 0f && second <= 1f) { fraction = second; return true; }

        return false;
    }

    public static Vector2 ToXZ(Vector3 v) => new Vector2(v.x, v.z);
    public static Vector3 FromXZ(Vector2 v, float y) => new Vector3(v.x, y, v.y);

    public static float ClosestApproachOffset(Vector2 sourceStart, Vector2 sourceDir, Vector2 targetCentre, float combinedRadius)
    {
        if (combinedRadius <= 0f) return 1f;

        Vector2 toTarget = targetCentre - sourceStart;
        float dirSqrMag = sourceDir.sqrMagnitude;
        if (dirSqrMag < 0.000001f) return 1f;

        float projection = Vector2.Dot(toTarget, sourceDir) / dirSqrMag;
        if (projection <= 0f) return 1f;

        Vector2 closestPoint = sourceStart + sourceDir * projection;
        float closestDist = Vector2.Distance(closestPoint, targetCentre);
        return Mathf.Clamp01(closestDist / combinedRadius);
    }
}