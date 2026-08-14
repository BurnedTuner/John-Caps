using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Aim-preview predictor that mirrors <see cref="Cap.HandleStackPeelOff"/> +
/// <see cref="CapTurnResolver.ResolveLanding"/> chain logic, but operates on
/// simulated state without mutating any real Cap fields.
///
/// Predicts the FULL chain up to <see cref="CapTuning.MaximumChainLength"/>.
/// Each prediction is tagged with a <see cref="PredictionSource"/> (Direct,
/// Chain, or Stack) so the caller can apply depth limits and continuation
/// toggles differently for chain reactions vs stack peel-offs.
///
/// When a direct or chain hit lands on a STACK base, peels off every cap
/// in the stack (in the same drop order the real sim uses) and emits one
/// <see cref="CapPrediction"/> per cap with Source = Stack.
///
/// The drop order mirrors Cap.HandleStackPeelOff exactly:
///   For stack [B, S1, S2, S3, T] (bottom-to-top), the drop sequence is
///   T, B, S3, S1, S2 — alternating "drop top, re-reverse remainder".
///   Each iteration flips every cap still in the working stack, so a cap
///   dropped at iteration k has been flipped k times:
///   WillLandHeads = initial_IsHeads XOR (k is odd).
/// </summary>
public static class StackPeelOffPredictor
{
    public static void Predict(
        IReadOnlyList<Cap> allCaps,
        IReadOnlyList<CapPrediction> directHits,
        CapTuning tuning,
        List<CapPrediction> results)
    {
        results.Clear();
        if (directHits == null || directHits.Count == 0 || allCaps == null || allCaps.Count == 0) return;

        // Simulated per-cap state — does NOT mutate real Cap fields.
        var simPositions = new Dictionary<Cap, Vector2>(allCaps.Count);
        var simConsumed = new HashSet<Cap>();

        // Seed simPositions. For stacked caps, use the base's ground position
        // (their own GroundPosition is stale from before they were stacked).
        for (int i = 0; i < allCaps.Count; i++)
        {
            Cap c = allCaps[i];
            if (c == null || c.HasLeftGame) continue;
            if (!c.CanFlip) continue;
            Cap baseCap = c.FindStackBase();
            simPositions[c] = baseCap.GroundPosition;
        }

        for (int i = 0; i < directHits.Count; i++)
        {
            if (results.Count >= tuning.MaximumChainLength) return;
            CapPrediction hit = directHits[i];
            if (hit.Cap == null || simConsumed.Contains(hit.Cap)) continue;

            ProcessCapLaunch(
                hit.Cap, hit.StartPosition, hit.Direction,
                hit.Force, hit.TravelDistance, depth: 0,
                source: PredictionSource.Direct,
                simPositions, simConsumed, allCaps, tuning, results);
        }
    }

    // -----------------------------------------------------------------------
    // Single-cap launch — peel-off aware
    // -----------------------------------------------------------------------

    static void ProcessCapLaunch(
        Cap cap,
        Vector2 startPos,
        Vector2 direction,
        float force,
        float travelDistance,
        int depth,
        PredictionSource source,
        Dictionary<Cap, Vector2> simPositions,
        HashSet<Cap> simConsumed,
        IReadOnlyList<Cap> allCaps,
        CapTuning tuning,
        List<CapPrediction> results)
    {
        if (cap == null || simConsumed.Contains(cap)) return;

        // Walk to the base of the stack (defensive: cap should already be a base
        // because stacked caps are not in CapRegistry.AllCaps).
        Cap baseCap = cap.FindStackBase();
        Vector2 basePos = simPositions.TryGetValue(baseCap, out Vector2 p) ? p : baseCap.GroundPosition;

        // Build the full stack bottom-to-top: [base, ...StackedAbove]
        var stack = new List<Cap> { baseCap };
        IReadOnlyList<Cap> above = baseCap.StackedAbove;
        for (int i = 0; i < above.Count; i++)
            stack.Add(above[i]);

        if (stack.Count == 1)
        {
            // Single cap, no peel-off.
            simConsumed.Add(cap);
            Vector2 landingPos = startPos + direction * travelDistance;
            bool willLandHeads = !cap.IsHeads;
            results.Add(new CapPrediction(
                cap, depth, startPos, direction, force, travelDistance, willLandHeads, source));
            simPositions[cap] = landingPos;
            ProcessChainReaction(cap, landingPos, force, depth + 1,
                simPositions, simConsumed, allCaps, tuning, results);
        }
        else
        {
            PeelOffAndPredict(stack, startPos, direction, force, travelDistance, depth,
                simPositions, simConsumed, allCaps, tuning, results);
        }
    }

    // -----------------------------------------------------------------------
    // Stack peel-off — mirrors Cap.HandleStackPeelOff
    // -----------------------------------------------------------------------

    static void PeelOffAndPredict(
        List<Cap> stack,
        Vector2 startPos,
        Vector2 direction,
        float force,
        float travelDistance,
        int depth,
        Dictionary<Cap, Vector2> simPositions,
        HashSet<Cap> simConsumed,
        IReadOnlyList<Cap> allCaps,
        CapTuning tuning,
        List<CapPrediction> results)
    {
        Vector2 currentLanding = startPos + direction * travelDistance;
        int iteration = 0;
        var workingStack = new List<Cap>(stack);

        while (workingStack.Count > 0)
        {
            if (results.Count >= tuning.MaximumChainLength) return;
            iteration++;

            workingStack.Reverse();
            Cap dropCap = workingStack[0];

            if (dropCap == null || simConsumed.Contains(dropCap))
            {
                if (workingStack.Count <= 1) break;
                Cap newHead = workingStack[1];
                workingStack.RemoveAt(0);
                workingStack.RemoveAt(0);
                workingStack.Insert(0, newHead);
                currentLanding = currentLanding + direction * travelDistance;
                continue;
            }

            simConsumed.Add(dropCap);

            bool willLandHeads = (iteration % 2) == 1 ? !dropCap.IsHeads : dropCap.IsHeads;

            Vector2 toLanding = currentLanding - startPos;
            float dropTravel = toLanding.magnitude;
            Vector2 dropDir = dropTravel > 0.0001f ? toLanding / dropTravel : direction;

            results.Add(new CapPrediction(
                dropCap,
                depth + iteration - 1,
                startPos,
                dropDir,
                force,
                dropTravel,
                willLandHeads,
                PredictionSource.Stack));

            simPositions[dropCap] = currentLanding;

            ProcessChainReaction(dropCap, currentLanding, force, depth + iteration,
                simPositions, simConsumed, allCaps, tuning, results);

            if (workingStack.Count == 1)
                break;

            Cap nextHead = workingStack[1];
            workingStack.RemoveAt(0);
            workingStack.RemoveAt(0);
            workingStack.Insert(0, nextHead);

            currentLanding = currentLanding + direction * travelDistance;
        }
    }

    // -----------------------------------------------------------------------
    // Chain reaction — recursive, depth-unbounded (MaximumChainLength only)
    // -----------------------------------------------------------------------

    static void ProcessChainReaction(
        Cap sourceCap,
        Vector2 landingPos,
        float force,
        int depth,
        Dictionary<Cap, Vector2> simPositions,
        HashSet<Cap> simConsumed,
        IReadOnlyList<Cap> allCaps,
        CapTuning tuning,
        List<CapPrediction> results)
    {
        float slammerRadius = sourceCap.Parameters.Radius;

        for (int i = 0; i < allCaps.Count; i++)
        {
            if (results.Count >= tuning.MaximumChainLength) return;

            Cap cap = allCaps[i];
            if (cap == null || cap == sourceCap) continue;
            if (simConsumed.Contains(cap)) continue;
            if (!cap.CanFlip) continue;
            if (!simPositions.TryGetValue(cap, out Vector2 capPos)) continue;

            float combined = slammerRadius + cap.Parameters.Radius;
            float dist = Vector2.Distance(landingPos, capPos);
            if (dist > combined) continue;

            float normalizedOffset = combined > 0f ? Mathf.Clamp01(dist / combined) : 0f;
            float contactFactor = cap.GetContactFactor(normalizedOffset);
            float transferForce = force * cap.Parameters.PowerConversion;
            float travel = transferForce * contactFactor * tuning.ForceToTravelDistance;
            if (travel < tuning.MinimumFlightLength) continue;

            Vector2 dir = CapMath.VerticalImpactDirection(landingPos, capPos, Vector2.up);

            ProcessCapLaunch(
                cap, capPos, dir, transferForce, travel, depth,
                source: PredictionSource.Chain,
                simPositions, simConsumed, allCaps, tuning, results);
        }
    }
}
