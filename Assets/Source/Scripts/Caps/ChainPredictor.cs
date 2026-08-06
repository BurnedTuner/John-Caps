using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Side-effect-free chain reaction predictor for the hop-by-hop model.
/// Each cap uses its own Radius for overlap checks (supports caps of different sizes).
/// </summary>
public static class ChainPredictor
{
    public static void Predict(
        IReadOnlyList<Cap> caps,
        IReadOnlyList<CapPrediction> directHits,
        CapTuning tuning,
        int maximumDepth,
        List<CapPrediction> results)
    {
        results.Clear();
        if (directHits.Count == 0) return;

        var positions = new Dictionary<Cap, Vector2>(caps.Count);
        for (int i = 0; i < caps.Count; i++)
            positions[caps[i]] = caps[i].GroundPosition;

        for (int i = 0; i < directHits.Count; i++)
        {
            if (results.Count >= tuning.MaximumChainLength) return;

            var hit = directHits[i];
            results.Add(hit);

            Vector2 landingPos = hit.StartPosition + hit.Direction * hit.TravelDistance;
            positions[hit.Cap] = landingPos;

            ProcessLandingRecursive(
                hit.Cap, landingPos, hit.Force, 1,
                caps, positions, tuning, maximumDepth, results);
        }
    }

    static void ProcessLandingRecursive(
        Cap landedCap,
        Vector2 landingPos,
        float force,
        int depth,
        IReadOnlyList<Cap> caps,
        Dictionary<Cap, Vector2> positions,
        CapTuning tuning,
        int maximumDepth,
        List<CapPrediction> results)
    {
        if (depth > maximumDepth) return;
        if (results.Count >= tuning.MaximumChainLength) return;

        float slammerRadius = landedCap.Parameters.Radius;

        for (int i = 0; i < caps.Count; i++)
        {
            if (results.Count >= tuning.MaximumChainLength) return;

            Cap cap = caps[i];
            if (cap == landedCap) continue;
            if (!positions.TryGetValue(cap, out Vector2 capPos)) continue;

            float combined = slammerRadius + cap.Parameters.Radius;
            float dist = Vector2.Distance(landingPos, capPos);
            if (dist > combined) continue;

            float normalizedOffset = combined > 0f ? Mathf.Clamp01(dist / combined) : 0f;
            float contactFactor = cap.GetContactFactor(normalizedOffset);
            float transferForce = force * cap.Parameters.PowerConversion * contactFactor;

            if (transferForce < tuning.MinimumFlightForce) continue;

            Vector2 direction = CapMath.VerticalImpactDirection(landingPos, capPos, Vector2.up);
            float travel = transferForce * tuning.ForceToTravelDistance;

            results.Add(new CapPrediction(cap, depth, capPos, direction, transferForce, travel));

            Vector2 capLandingPos = capPos + direction * travel;
            positions[cap] = capLandingPos;

            ProcessLandingRecursive(
                cap, capLandingPos, transferForce, depth + 1,
                caps, positions, tuning, maximumDepth, results);
        }
    }
}