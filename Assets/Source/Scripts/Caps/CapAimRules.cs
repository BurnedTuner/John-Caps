using System.Collections.Generic;
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

    /// <summary>
    /// True when a cap of this radius landing on the point would overlap with an
    /// active <see cref="DefenderCapEffect"/> zone that blocks the given thrower.
    ///
    /// Defender zones are circular, centered on each defender cap's GroundPosition,
    /// with radius <see cref="DefenderCapEffect.ZoneRadius"/>. The check accounts
    /// for the landing cap's own radius — the point is blocked if the distance
    /// between the landing center and the defender center is less than
    /// (defender.ZoneRadius + capRadius).
    ///
    /// Only ACTIVE defenders are considered: the cap must be on the field and
    /// showing the correct side (see <see cref="DefenderCapEffect.IsZoneActive"/>).
    /// </summary>
    public static bool IsBlockedByDefenderCap(Vector3 point, float capRadius, CapOwner throwerOwner)
    {
        Vector2 point2D = CapMath.ToXZ(point);
        float radius = Mathf.Max(0.01f, capRadius);

        IReadOnlyList<Cap> allCaps = CapRegistry.AllCaps;
        for (int i = 0; i < allCaps.Count; i++)
        {
            Cap cap = allCaps[i];
            if (cap == null) continue;

            DefenderCapEffect defender = cap.GetComponent<DefenderCapEffect>();
            if (defender == null) continue;
            if (!defender.IsZoneActive()) continue;
            if (!defender.BlocksThrower(throwerOwner)) continue;

            // Circle-circle overlap: the thrown cap (radius = capRadius) touches
            // the defender zone (radius = ZoneRadius) when the distance between
            // their centers is less than the sum of their radii. This blocks
            // ANY part of the cap from touching the zone, not just the center.
            float distance = Vector2.Distance(point2D, defender.ZoneCenter);
            if (distance < defender.ZoneRadius + radius)
                return true;
        }

        return false;
    }
}
