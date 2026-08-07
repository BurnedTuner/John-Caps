using System.Collections.Generic;
using UnityEngine;

internal sealed class CapRegistryEffectQuery : ICapEffectQuery
{
    public void CollectCapsInRadius(Vector2 center, float radius, List<Cap> results)
    {
        results.Clear();
        if (radius <= 0f) return;

        float radiusSquared = radius * radius;
        for (int i = 0; i < CapRegistry.AllCaps.Count; i++)
        {
            Cap cap = CapRegistry.AllCaps[i];
            if (cap == null) continue;

            Vector2 offset = cap.GroundPosition - center;
            if (offset.sqrMagnitude < radiusSquared)
                results.Add(cap);
        }
    }
}
