using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Aim-preview predictor that mirrors <see cref="Cap.HandleStackPeelOff"/> +
/// <see cref="CapTurnResolver.ResolveLanding"/> chain logic, but operates on
/// simulated state without mutating any real Cap fields.
///
/// Tracks simulated stack state: when a cap is peeled off, it's removed from
/// the simulated stack so subsequent chain reactions see the correct (smaller)
/// stack. Caps still in the stack during peel-off are excluded from chain
/// reactions (they're in flight, not at rest).
/// </summary>
public static class StackPeelOffPredictor
{
    public static void Predict(
        IReadOnlyList<Cap> allCaps,
        IReadOnlyList<CapPrediction> directHits,
        CapTuning tuning,
        List<CapPrediction> results,
        Cap thrownCap = null,
        Vector2 thrownLandingPos = default)
    {
        results.Clear();
        if (directHits == null || directHits.Count == 0 || allCaps == null || allCaps.Count == 0) return;

        var simPositions = new Dictionary<Cap, Vector2>(allCaps.Count);
        var simIsHeads = new Dictionary<Cap, bool>(allCaps.Count);
        // Tracks caps that have been peeled off from a stack in the simulation.
        var simPeeledOff = new HashSet<Cap>();
        // Tracks caps that are currently IN the stack (still flying during
        // peel-off). These should NOT be hit by chain reactions.
        var simInStack = new HashSet<Cap>();
        // Simulated stack-above lists. Key = base cap, Value = list of caps
        // still above it in the simulation (may shrink as caps are peeled off).
        var simStackAbove = new Dictionary<Cap, List<Cap>>();

        // Seed simPositions + simIsHeads + simStackAbove + simInStack.
        for (int i = 0; i < allCaps.Count; i++)
        {
            Cap c = allCaps[i];
            if (c == null || c.HasLeftGame) continue;
            if (c == thrownCap) continue;

            Cap baseCap = c.FindStackBase();
            simPositions[c] = baseCap.GroundPosition;
            simIsHeads[c] = c.IsHeads;

            if (c.StackBase != null)
                simInStack.Add(c);
        }

        // Build simulated stack-above lists from the real StackedAbove.
        for (int i = 0; i < allCaps.Count; i++)
        {
            Cap c = allCaps[i];
            if (c == null || c.HasLeftGame) continue;
            if (c == thrownCap) continue;
            if (c.StackBase != null) continue; // only bases have StackedAbove

            var list = new List<Cap>();
            IReadOnlyList<Cap> above = c.StackedAbove;
            for (int a = 0; a < above.Count; a++)
                list.Add(above[a]);
            simStackAbove[c] = list;
        }

        // Seed the thrown cap at its landing position.
        if (thrownCap != null)
        {
            simPositions[thrownCap] = thrownLandingPos;
            simIsHeads[thrownCap] = !thrownCap.IsHeads;
            simStackAbove[thrownCap] = new List<Cap>();
        }

        for (int i = 0; i < directHits.Count; i++)
        {
            if (results.Count >= tuning.MaximumChainLength) return;
            CapPrediction hit = directHits[i];
            if (hit.Cap == null) continue;

            ProcessCapLaunch(
                hit.Cap, hit.StartPosition, hit.Direction,
                hit.Force, hit.TravelDistance, depth: 0,
                source: PredictionSource.Direct,
                simPositions, simIsHeads, simPeeledOff, simInStack, simStackAbove,
                allCaps, tuning, results);
        }
    }

    static void ProcessCapLaunch(
        Cap cap,
        Vector2 startPos,
        Vector2 direction,
        float force,
        float travelDistance,
        int depth,
        PredictionSource source,
        Dictionary<Cap, Vector2> simPositions,
        Dictionary<Cap, bool> simIsHeads,
        HashSet<Cap> simPeeledOff,
        HashSet<Cap> simInStack,
        Dictionary<Cap, List<Cap>> simStackAbove,
        IReadOnlyList<Cap> allCaps,
        CapTuning tuning,
        List<CapPrediction> results)
    {
        if (cap == null) return;

        // Build the simulated stack: [cap, ...simStackAbove[cap]]
        // Use the SIMULATED stack list, not the real StackedAbove — caps that
        // were peeled off in a prior iteration are removed from simStackAbove.
        var stack = new List<Cap> { cap };
        if (simStackAbove.TryGetValue(cap, out var above))
        {
            for (int i = 0; i < above.Count; i++)
            {
                if (above[i] != null && !simPeeledOff.Contains(above[i]))
                    stack.Add(above[i]);
            }
        }

        if (stack.Count <= 1)
        {
            // Single cap, no peel-off.
            Vector2 landingPos = startPos + direction * travelDistance;
            bool currentIsHeads = simIsHeads.TryGetValue(cap, out bool h) ? h : cap.IsHeads;
            bool willLandHeads = !currentIsHeads;
            simIsHeads[cap] = willLandHeads;
            results.Add(new CapPrediction(
                cap, depth, startPos, direction, force, travelDistance, willLandHeads, source));
            simPositions[cap] = landingPos;
            simPeeledOff.Add(cap);
            ProcessChainReaction(cap, landingPos, force, depth + 1,
                simPositions, simIsHeads, simPeeledOff, simInStack, simStackAbove,
                allCaps, tuning, results);
        }
        else
        {
            PeelOffAndPredict(stack, startPos, direction, force, travelDistance, depth,
                simPositions, simIsHeads, simPeeledOff, simInStack, simStackAbove,
                allCaps, tuning, results);
        }
    }

    static void PeelOffAndPredict(
        List<Cap> stack,
        Vector2 startPos,
        Vector2 direction,
        float force,
        float travelDistance,
        int depth,
        Dictionary<Cap, Vector2> simPositions,
        Dictionary<Cap, bool> simIsHeads,
        HashSet<Cap> simPeeledOff,
        HashSet<Cap> simInStack,
        Dictionary<Cap, List<Cap>> simStackAbove,
        IReadOnlyList<Cap> allCaps,
        CapTuning tuning,
        List<CapPrediction> results)
    {
        Vector2 currentLanding = startPos + direction * travelDistance;
        Vector2 prevLanding = startPos;
        int iteration = 0;
        var workingStack = new List<Cap>(stack);

        // Mark all stack members as "in stack" so ProcessChainReaction skips
        // them (they're flying, not at rest).
        Cap stackBase = stack[0];
        for (int i = 0; i < workingStack.Count; i++)
        {
            if (workingStack[i] != null)
                simInStack.Add(workingStack[i]);
        }

        while (workingStack.Count > 0)
        {
            if (results.Count >= tuning.MaximumChainLength) return;
            iteration++;

            workingStack.Reverse();
            Cap dropCap = workingStack[0];

            if (dropCap == null)
            {
                if (workingStack.Count <= 1) break;
                Cap newHead = workingStack[1];
                workingStack.RemoveAt(0);
                workingStack.RemoveAt(0);
                workingStack.Insert(0, newHead);
                prevLanding = currentLanding;
                currentLanding = currentLanding + direction * travelDistance;
                continue;
            }

            // Remove from simInStack — this cap is now being dropped.
            simInStack.Remove(dropCap);

            // In the real runtime, StepFly toggles IsHeads for ALL caps in the
            // stack (base + all stacked above) BEFORE peeling off. We must do
            // the same here: toggle simIsHeads for every cap still in
            // workingStack, not just the dropped one. This ensures the next
            // iteration's dropCap has the correct accumulated flip count.
            for (int i = 0; i < workingStack.Count; i++)
            {
                Cap c = workingStack[i];
                if (c == null) continue;
                bool curH = simIsHeads.TryGetValue(c, out bool ch) ? ch : c.IsHeads;
                simIsHeads[c] = !curH;
            }

            // The dropped cap's WillLandHeads is the toggled value we just set.
            bool willLandHeads = simIsHeads.TryGetValue(dropCap, out bool h) ? h : dropCap.IsHeads;

            Vector2 toLanding = currentLanding - prevLanding;
            float dropTravel = toLanding.magnitude;
            Vector2 dropDir = dropTravel > 0.0001f ? toLanding / dropTravel : direction;

            results.Add(new CapPrediction(
                dropCap,
                depth + iteration - 1,
                prevLanding,
                dropDir,
                force,
                dropTravel,
                willLandHeads,
                PredictionSource.Stack));

            simPositions[dropCap] = currentLanding;
            simPeeledOff.Add(dropCap);

            // Remove this cap from the simulated stack-above list of the base.
            if (simStackAbove.TryGetValue(stackBase, out var aboveList))
                aboveList.Remove(dropCap);

            ProcessChainReaction(dropCap, currentLanding, force, depth + iteration,
                simPositions, simIsHeads, simPeeledOff, simInStack, simStackAbove,
                allCaps, tuning, results);

            if (workingStack.Count == 1)
            {
                // The last cap is the new head — it's no longer in a stack.
                Cap lastCap = workingStack[0];
                simInStack.Remove(lastCap);
                break;
            }

            Cap nextHead = workingStack[1];
            workingStack.RemoveAt(0);
            workingStack.RemoveAt(0);
            workingStack.Insert(0, nextHead);

            prevLanding = currentLanding;
            currentLanding = currentLanding + direction * travelDistance;
        }
    }

    static void ProcessChainReaction(
        Cap sourceCap,
        Vector2 landingPos,
        float force,
        int depth,
        Dictionary<Cap, Vector2> simPositions,
        Dictionary<Cap, bool> simIsHeads,
        HashSet<Cap> simPeeledOff,
        HashSet<Cap> simInStack,
        Dictionary<Cap, List<Cap>> simStackAbove,
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
            // Skip caps still in the stack (they're flying, not at rest).
            if (simInStack.Contains(cap)) continue;
            // A cap is flippable if its real CanFlip is true OR it has been
            // peeled off in the simulation.
            if (!cap.CanFlip && !simPeeledOff.Contains(cap)) continue;
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
                simPositions, simIsHeads, simPeeledOff, simInStack, simStackAbove,
                allCaps, tuning, results);
        }
    }
}
