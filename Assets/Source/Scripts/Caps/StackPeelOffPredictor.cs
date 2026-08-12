using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Aim-preview predictor that mirrors <see cref="Cap.HandleStackPeelOff"/> +
/// <see cref="CapTurnResolver.ResolveLanding"/> chain logic, but operates on
/// simulated state without mutating any real Cap fields.
///
/// Differences from <see cref="ChainPredictor"/>:
///   - Ignores <see cref="CapTuning.PredictionDepth"/> — predicts the full
///     chain up to <see cref="CapTuning.MaximumChainLength"/> only.
///   - When a direct or chain hit lands on a STACK base, peels off every cap
///     in the stack (in the same drop order the real sim uses) and emits one
///     <see cref="CapPrediction"/> per cap.
///   - Computes <see cref="CapPrediction.WillLandHeads"/> for every prediction
///     so the ghost-preview system can render the correct side.
///
/// The drop order mirrors Cap.HandleStackPeelOff exactly:
///   For stack [B, S1, S2, S3, T] (bottom-to-top), the drop sequence is
///   T, B, S3, S1, S2 — alternating "drop top, re-reverse remainder".
///   Each iteration flips every cap still in the working stack, so a cap
///   dropped at iteration k has been flipped k times:
///   WillLandHeads = initial_IsHeads XOR (k is odd).
///
/// Known V1 limitation: when a peel-off cap lands on another cap with
/// insufficient force to launch it, the real sim calls AddToStack (stacking
/// the cap on top). This predictor pretends nothing happens — the cap just
/// lands and stops. Acceptable for V1 ghost preview.
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

        // Build the full stack bottom-to-top: [base, ...StackAbove]
        // (StackAbove returns bottom-to-top per Cap.cs).
        var stack = new List<Cap> { baseCap };
        IReadOnlyList<Cap> above = baseCap.StackAbove;
        for (int i = 0; i < above.Count; i++)
            stack.Add(above[i]);

        if (stack.Count == 1)
        {
            // Single cap, no peel-off.
            simConsumed.Add(cap);
            Vector2 landingPos = startPos + direction * travelDistance;
            bool willLandHeads = !cap.IsHeads; // StepFly flips IsHeads once on landing
            results.Add(new CapPrediction(
                cap, depth, startPos, direction, force, travelDistance, willLandHeads));
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
        // stack is bottom-to-top: [base, S1, S2, ..., T]
        // All landing positions lie on a straight line, each spaced travelDistance apart.
        Vector2 currentLanding = startPos + direction * travelDistance; // L1 = base's own landing
        int iteration = 0;
        var workingStack = new List<Cap>(stack);

        while (workingStack.Count > 0)
        {
            if (results.Count >= tuning.MaximumChainLength) return;
            iteration++;

            // Reverse to get top-first order (matches HandleStackPeelOff line 375).
            workingStack.Reverse();
            Cap dropCap = workingStack[0];

            if (dropCap == null || simConsumed.Contains(dropCap))
            {
                if (workingStack.Count <= 1) break;
                // Remove dropCap and continue with the remainder as new stack.
                Cap newHead = workingStack[1];
                workingStack.RemoveAt(0);
                workingStack.RemoveAt(0);
                workingStack.Insert(0, newHead);
                currentLanding = currentLanding + direction * travelDistance;
                continue;
            }

            simConsumed.Add(dropCap);

            // dropCap flips once per iteration it survives. Caps dropped at iteration k
            // have been flipped k times. WillLandHeads = initial XOR (k is odd).
            bool willLandHeads = (iteration % 2) == 1 ? !dropCap.IsHeads : dropCap.IsHeads;

            // For the trajectory LINE, draw from the stack's pre-launch position
            // (startPos) to this cap's final landing. Direction + travelDistance
            // are recomputed so EndPosition == currentLanding exactly.
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
                willLandHeads));

            simPositions[dropCap] = currentLanding;

            // Chain reaction at this landing (may hit OTHER base caps/stacks).
            ProcessChainReaction(dropCap, currentLanding, force, depth + iteration,
                simPositions, simConsumed, allCaps, tuning, results);

            if (workingStack.Count == 1)
                break; // last cap dropped, no more flight

            // Set up next iteration: newHead = workingStack[1], newStack = [newHead] + workingStack[2..]
            // This mirrors Cap.cs:398-406.
            Cap nextHead = workingStack[1];
            workingStack.RemoveAt(0); // remove dropCap
            workingStack.RemoveAt(0); // remove nextHead from its old position
            workingStack.Insert(0, nextHead);

            // Next flight launches from currentLanding, lands at currentLanding + dir*travel.
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
                simPositions, simConsumed, allCaps, tuning, results);
        }
    }
}
