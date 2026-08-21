using System.Collections.Generic;
using UnityEngine;

internal sealed class CapRegistryEffectQuery : ICapEffectQuery
{
    public void CollectCapsInRadius(Vector2 center, float radius, List<Cap> results)
    {
        results.Clear();
        if (radius <= 0f) return;

        for (int i = 0; i < CapRegistry.AllCaps.Count; i++)
        {
            Cap cap = CapRegistry.AllCaps[i];
            if (cap == null) continue;

            // Circle–circle overlap: a cap is "in radius" when ANY part of its
            // body touches the effect circle — not only when its center is
            // inside. Effect radius + the cap's own radius defines the touch
            // distance, so a cap whose center is outside the effect but whose
            // rim still intersects it is included.
            //
            // This matches the defender-cap aim-block test (CapAimRules uses the
            // same `radius + capRadius` math) and is what players expect: "if
            // any part of my cap touches the explosion, I get hit."
            Vector2 offset = cap.GroundPosition - center;
            float touch = radius + cap.Parameters.Radius;
            if (offset.sqrMagnitude < touch * touch)
                results.Add(cap);
        }
    }
}
