using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Read-only access to caps visible to the effect simulation.
/// </summary>
public interface ICapEffectQuery
{
    /// <summary>Clears and fills the caller-owned result buffer.</summary>
    void CollectCapsInRadius(Vector2 center, float radius, List<Cap> results);
}
