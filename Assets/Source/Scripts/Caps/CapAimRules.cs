using UnityEngine;

/// <summary>
/// Rules that decide whether a cap may be aimed at a landing point.
/// Shared by the player's CapThrower and by the AI move search so both obey the same restrictions.
/// </summary>
public static class CapAimRules
{
    private const int OverlapBufferSize = 32;

    private static readonly Collider[] _overlapBuffer = new Collider[OverlapBufferSize];

    /// <summary>
    /// True when a cap of this radius landing on the point would sit inside a scoring zone
    /// that forbids direct aiming.
    /// </summary>
    public static bool IsBlockedByScoringZone(Vector3 point, float capRadius)
    {
        int hitCount = Physics.OverlapSphereNonAlloc(
            point,
            Mathf.Max(0.01f, capRadius),
            _overlapBuffer,
            ~0,
            QueryTriggerInteraction.Collide);

        for (int i = 0; i < hitCount; i++)
        {
            Collider hit = _overlapBuffer[i];
            if (hit == null) continue;

            ScoringZone scoringZone = hit.GetComponentInParent<ScoringZone>();
            if (scoringZone != null && scoringZone.BlocksDirectAiming)
                return true;
        }

        return false;
    }
}
