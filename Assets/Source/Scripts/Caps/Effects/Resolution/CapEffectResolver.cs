using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Builds and applies effect commands immediately at a safe point in the current simulation step.
///
/// Resolution strategy:
///   1. Walk the source cap's <see cref="CapFlipEffect"/>s and let each one enqueue
///      <see cref="ICapEffectCommand"/>s into a buffer (Push / Flip / Launch).
///   2. MERGE the buffer into a per-target action map. A target may be claimed by
///      more than one command (e.g., a cap with both Bomb and Flipper effects will
///      emit a Push for the bomb radius and a Flip for the flipper radius). The
///      merge decides, per target, which single action actually executes:
///        - Push + Flip  → Launch (flips AND moves with the bomb's force). This is
///                          the "in both radii" combined case.
///        - Push only     → Push (slides without flipping).
///        - Flip only     → Flip in place.
///        - Launch (any)  → Launch (Launch already does both move + flip; it wins
///                          over Push/Flip because it strictly supersedes them).
///      Targets already busy or out of the game are dropped at merge time, so the
///      executors never see a target they would refuse — that means the
///      "Push-sets-IsBusy-then-Flip-is-skipped" race that used to silently drop the
///      flip no longer happens.
///   3. Execute the merged per-target actions.
/// </summary>
internal sealed class CapEffectResolver
{
    private readonly ICapEffectQuery _query;
    private readonly ICapEffectCommandExecutor _executor;
    private readonly CapEffectCommandBuffer _commandBuffer = new();
    private readonly List<Cap> _targets = new();

    // Per-target merged action. A struct so we can value-update via the dictionary
    // without per-entry allocations.
    struct ResolvedTarget
    {
        public Cap Target;
        public Cap Source;          // last effect source that touched this target
        public bool WantsPush;      // bomb-style "move without flipping"
        public bool WantsFlip;      // flipper-style "flip in place"
        public Vector2 MoveDirection;
        public float MoveForce;
    }

    private readonly Dictionary<Cap, ResolvedTarget> _resolved = new();
    private readonly List<Cap> _resolvedOrder = new();

    public CapEffectResolver(ICapEffectQuery query, ICapEffectCommandExecutor executor)
    {
        _query = query;
        _executor = executor;
    }

    public void ResolveImmediate(in CapFlipEvent flipEvent)
    {
        if (flipEvent.Source == null) return;

        _commandBuffer.Clear();
        CapFlipEffect[] effects = flipEvent.Source.FlipEffects;

        if (effects != null)
        {
            for (int i = 0; i < effects.Length; i++)
            {
                CapFlipEffect effect = effects[i];
                if (effect != null && effect.isActiveAndEnabled)
                    effect.BuildCommands(flipEvent, _query, _commandBuffer);
            }
        }

        // MERGE: collapse the command buffer into a per-target action map.
        // After this loop, each target that any command wanted to touch has
        // exactly one ResolvedTarget entry summarising what should happen.
        IReadOnlyList<ICapEffectCommand> commands = _commandBuffer.Commands;
        for (int i = 0; i < commands.Count; i++)
            MergeCommand(commands[i]);

        _commandBuffer.Clear();

        // EXECUTE: one action per target. Order is insertion-stable so the
        // behaviour is deterministic across frames.
        for (int i = 0; i < _resolvedOrder.Count; i++)
        {
            Cap target = _resolvedOrder[i];
            if (target == null) continue;

            ResolvedTarget resolved = _resolved[target];

            if (resolved.WantsPush && resolved.WantsFlip)
            {
                // Combined "in both radii" case: launch with the bomb's force.
                // Launch flips the cap AND moves it the bomb-force distance,
                // which is exactly the requested behaviour ("flipped not in
                // place but with the bomb force distance").
                _executor.TryLaunch(resolved.Source, target, resolved.MoveDirection, resolved.MoveForce);
            }
            else if (resolved.WantsPush)
            {
                _executor.TryPush(resolved.Source, target, resolved.MoveDirection, resolved.MoveForce);
            }
            else if (resolved.WantsFlip)
            {
                _executor.TryFlip(resolved.Source, target);
            }
            // else: target was claimed by Launch only — Launch is already a
            // complete move+flip, handled below in MergeCommand's launch branch
            // by setting both WantsPush and WantsFlip, so we never get here.
        }

        _resolved.Clear();
        _resolvedOrder.Clear();
    }

    /// <summary>
    /// Fold one command's targets into the per-target action map. Idempotent for
    /// the same target — multiple commands wanting the same action just reaffirm
    /// it. Different actions (Push + Flip) escalate the merged action to a Launch.
    /// </summary>
    void MergeCommand(ICapEffectCommand command)
    {
        if (command is RadialPushCommand push)
            MergePush(push);
        else if (command is RadialFlipCommand flip)
            MergeFlip(flip);
        else if (command is RadialLaunchCommand launch)
            MergeLaunch(launch);
        else if (command != null)
            Debug.LogError($"[CapEffectResolver] Unsupported command: {command.GetType().Name}.");
    }

    void MergePush(RadialPushCommand command)
    {
        if (command.Source == null || command.Radius <= 0f || command.Force <= 0f) return;

        Cap sourceStackBase = command.Source.StackBase;

        _query.CollectCapsInRadius(command.Origin, command.Radius, _targets);

        for (int i = 0; i < _targets.Count; i++)
        {
            Cap target = _targets[i];
            if (target == null || target == command.Source) continue;
            if (target.IsBusy || target.HasLeftGame) continue;
            // The cap the source LANDED ON is now its stack base. It must not
            // be affected by the source's own effect — it is pinned under the
            // source and should not be pushed/flipped/launched away.
            if (target == sourceStackBase) continue;
            // Also skip the cap the source landed on when the stack base
            // hasn't been set yet. This happens for chain-launched caps:
            // ResolvePendingFlipEffects runs BEFORE ResolvePendingLandings,
            // so the landing hasn't resolved and AddToStack hasn't run →
            // sourceStackBase is null. Without this check, the cap gets
            // pushed by the effect, then when the landing tries to launch it
            // the cap is busy and the launch fails — leaving them overlapping.
            // The cap the source landed on is at the source's landing position
            // (= command.Origin), within half its own radius.
            float landedThreshold = target.Parameters.Radius * 0.5f;
            if ((target.GroundPosition - command.Origin).sqrMagnitude
                < landedThreshold * landedThreshold)
                continue;

            Vector2 offset = target.GroundPosition - command.Origin;
            Vector2 dir = offset.sqrMagnitude > 0.000001f ? offset.normalized : Vector2.right;

            bool found = _resolved.TryGetValue(target, out ResolvedTarget resolved);
            if (!found)
            {
                resolved = new ResolvedTarget { Target = target };
                _resolvedOrder.Add(target);
            }
            resolved.WantsPush = true;
            resolved.MoveForce = command.Force;
            resolved.MoveDirection = dir;
            resolved.Source = command.Source;
            _resolved[target] = resolved;
        }

        _targets.Clear();
    }

    void MergeFlip(RadialFlipCommand command)
    {
        if (command.Source == null || command.Radius <= 0f) return;

        Cap sourceStackBase = command.Source.StackBase;

        _query.CollectCapsInRadius(command.Origin, command.Radius, _targets);

        for (int i = 0; i < _targets.Count; i++)
        {
            Cap target = _targets[i];
            if (target == null || target == command.Source) continue;
            if (target.IsBusy || target.HasLeftGame) continue;
            // The cap the source LANDED ON is now its stack base. It must not
            // be affected by the source's own effect — it is pinned under the
            // source and should not be pushed/flipped/launched away.
            if (target == sourceStackBase) continue;
            // Also skip the cap the source landed on when the stack base
            // hasn't been set yet. This happens for chain-launched caps:
            // ResolvePendingFlipEffects runs BEFORE ResolvePendingLandings,
            // so the landing hasn't resolved and AddToStack hasn't run →
            // sourceStackBase is null. Without this check, the cap gets
            // flipped in place by the flipper effect, then when the landing
            // tries to launch it the cap is busy and the launch fails —
            // leaving them overlapping.
            // The cap the source landed on is at the source's landing position
            // (= command.Origin), within half its own radius.
            float landedThreshold = target.Parameters.Radius * 0.5f;
            if ((target.GroundPosition - command.Origin).sqrMagnitude
                < landedThreshold * landedThreshold)
                continue;

            bool found = _resolved.TryGetValue(target, out ResolvedTarget resolved);
            if (!found)
            {
                resolved = new ResolvedTarget { Target = target };
                _resolvedOrder.Add(target);
            }
            resolved.WantsFlip = true;
            // Keep the existing Source/MoveForce/MoveDirection if a push already
            // set them — we want the bomb's parameters to drive the combined
            // launch, not the flipper's (which has no force of its own).
            if (resolved.Source == null)
                resolved.Source = command.Source;
            _resolved[target] = resolved;
        }

        _targets.Clear();
    }

    void MergeLaunch(RadialLaunchCommand command)
    {
        if (command.Source == null || command.Radius <= 0f || command.Force <= 0f) return;

        Cap sourceStackBase = command.Source.StackBase;

        _query.CollectCapsInRadius(command.Origin, command.Radius, _targets);

        for (int i = 0; i < _targets.Count; i++)
        {
            Cap target = _targets[i];
            if (target == null || target == command.Source) continue;
            if (target.IsBusy || target.HasLeftGame) continue;
            // The cap the source LANDED ON is now its stack base. It must not
            // be affected by the source's own effect — it is pinned under the
            // source and should not be pushed/flipped/launched away.
            if (target == sourceStackBase) continue;
            // Also skip the cap the source landed on when the stack base
            // hasn't been set yet (chain-launched caps — see MergeFlip for
            // the full explanation).
            float landedThreshold = target.Parameters.Radius * 0.5f;
            if ((target.GroundPosition - command.Origin).sqrMagnitude
                < landedThreshold * landedThreshold)
                continue;

            Vector2 offset = target.GroundPosition - command.Origin;
            Vector2 dir = offset.sqrMagnitude > 0.000001f ? offset.normalized : Vector2.right;

            bool found = _resolved.TryGetValue(target, out ResolvedTarget resolved);
            if (!found)
            {
                resolved = new ResolvedTarget { Target = target };
                _resolvedOrder.Add(target);
            }
            // A Launch already does both move and flip. Marking both flags means
            // the executor dispatches it through TryLaunch, which is the
            // strongest action available — strictly dominates Push or Flip alone.
            resolved.WantsPush = true;
            resolved.WantsFlip = true;
            resolved.MoveForce = command.Force;
            resolved.MoveDirection = dir;
            resolved.Source = command.Source;
            _resolved[target] = resolved;
        }

        _targets.Clear();
    }
}
