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
///
/// "Landed-on" cap exclusion:
///   When a cap lands on top of another cap (creating a stack), the landed-on cap
///   must NOT be affected by the source's own effect — it's the cap the source is
///   physically resting on. The primary exclusion is the stack-base check
///   (<c>target == sourceStackBase</c> / <c>target.FindStackBase() == sourceFindBase</c>).
///   The secondary fallback is a DISTANCE check: if the source's landing position
///   is within the COMBINED radius (source radius + target radius) of the target's
///   center, the source "landed on" that target and the target is skipped.
///
///   IMPORTANT: the distance threshold MUST be the combined radius, not half the
///   target's radius. The old threshold (target.radius * 0.5) only excluded caps
///   whose CENTER the source landed within — if the source landed on the outer
///   half of the target's body (within the target's radius but more than half a
///   radius from its center), the target was NOT excluded and got flipped in place
///   by the flipper's effect. This caused the "flipper landed on a cap but flipped
///   it in place instead of launching it" bug.
/// </summary>
internal sealed class CapEffectResolver
{
    private readonly ICapEffectQuery _query;
    private readonly ICapEffectCommandExecutor _executor;
    private readonly CapEffectCommandBuffer _commandBuffer = new();
    private readonly List<Cap> _targets = new();

    struct ResolvedTarget
    {
        public Cap Target;
        public Cap Source;
        public bool WantsPush;
        public bool WantsFlip;
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

        IReadOnlyList<ICapEffectCommand> commands = _commandBuffer.Commands;
        for (int i = 0; i < commands.Count; i++)
            MergeCommand(commands[i]);

        _commandBuffer.Clear();

        for (int i = 0; i < _resolvedOrder.Count; i++)
        {
            Cap target = _resolvedOrder[i];
            if (target == null) continue;

            ResolvedTarget resolved = _resolved[target];

            if (resolved.WantsPush && resolved.WantsFlip)
            {
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
        }

        _resolved.Clear();
        _resolvedOrder.Clear();
    }

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

    /// <summary>
    /// Returns true if the target cap is the one the source LANDED ON (i.e., the
    /// source's landing position overlaps the target's body). Such caps are
    /// excluded from the source's own effect — they should be affected by the
    /// IMPACT (launch/stack), not by the radial effect.
    ///
    /// The check uses the COMBINED radius (source radius + target radius) as the
    /// threshold: if the distance between the source's landing position and the
    /// target's center is less than this, the two caps overlap and the target is
    /// considered "landed on".
    /// </summary>
    static bool IsLandedOnCap(Cap target, Cap source, Vector2 origin)
    {
        if (target == null || source == null) return false;

        // Primary check: stack base. If AddToStack was called, the source's
        // _stackBase points to the cap it's resting on (or the bottom of that
        // cap's stack). This is the authoritative "landed on" signal.
        Cap sourceStackBase = source.StackBase;
        if (target == sourceStackBase) return true;

        // Same-stack check: if the target is in the same stack as the source,
        // it's either the cap the source landed on or a cap stacked above/below
        // it — either way, don't affect it.
        Cap sourceFindBase = source.FindStackBase();
        if (target.FindStackBase() == sourceFindBase) return true;

        // Fallback: distance check. The stack-base check only works if AddToStack
        // was called (low-force landing → stack). For high-force landings where
        // the cap was LAUNCHED (not stacked), the stack base isn't set — but the
        // launched cap is busy and excluded by IsBusy anyway. This distance check
        // catches the remaining edge case: the source landed on a cap that was
        // SKIPPED by ResolveLanding (e.g., a cap in an existing stack whose
        // CanFlip was false). In that case, AddToStack wasn't called on it, and
        // the cap isn't busy — without this check, the radial effect would flip
        // it in place.
        //
        // The threshold is the COMBINED radius (source + target): if the source's
        // landing position is within this distance of the target's center, the
        // two caps physically overlap and the source "landed on" the target.
        float targetRadius = target.Parameters != null ? target.Parameters.Radius : 0.5f;
        float sourceRadius = source.Parameters != null ? source.Parameters.Radius : 0.5f;
        float landedThreshold = targetRadius + sourceRadius;
        float sqrDistance = (target.GroundPosition - origin).sqrMagnitude;
        return sqrDistance < landedThreshold * landedThreshold;
    }

    void MergePush(RadialPushCommand command)
    {
        if (command.Source == null || command.Radius <= 0f || command.Force <= 0f) return;

        _query.CollectCapsInRadius(command.Origin, command.Radius, _targets);

        for (int i = 0; i < _targets.Count; i++)
        {
            Cap target = _targets[i];
            if (target == null || target == command.Source) continue;
            if (target.IsBusy || target.HasLeftGame) continue;
            // Exclude the cap the source LANDED ON — it's affected by the impact,
            // not by this radial effect.
            if (IsLandedOnCap(target, command.Source, command.Origin)) continue;

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

        _query.CollectCapsInRadius(command.Origin, command.Radius, _targets);

        for (int i = 0; i < _targets.Count; i++)
        {
            Cap target = _targets[i];
            if (target == null || target == command.Source) continue;
            if (target.IsBusy || target.HasLeftGame) continue;
            // Exclude the cap the source LANDED ON — it's affected by the impact,
            // not by this radial effect. Without this, a flipper landing on a cap
            // would flip that cap IN PLACE (TryFlip → BeginFlipInPlace) instead of
            // letting the impact launch it (TryActivateCap → BeginLaunch).
            if (IsLandedOnCap(target, command.Source, command.Origin)) continue;

            bool found = _resolved.TryGetValue(target, out ResolvedTarget resolved);
            if (!found)
            {
                resolved = new ResolvedTarget { Target = target };
                _resolvedOrder.Add(target);
            }
            resolved.WantsFlip = true;
            if (resolved.Source == null)
                resolved.Source = command.Source;
            _resolved[target] = resolved;
        }

        _targets.Clear();
    }

    void MergeLaunch(RadialLaunchCommand command)
    {
        if (command.Source == null || command.Radius <= 0f || command.Force <= 0f) return;

        _query.CollectCapsInRadius(command.Origin, command.Radius, _targets);

        for (int i = 0; i < _targets.Count; i++)
        {
            Cap target = _targets[i];
            if (target == null || target == command.Source) continue;
            if (target.IsBusy || target.HasLeftGame) continue;
            if (IsLandedOnCap(target, command.Source, command.Origin)) continue;

            Vector2 offset = target.GroundPosition - command.Origin;
            Vector2 dir = offset.sqrMagnitude > 0.000001f ? offset.normalized : Vector2.right;

            bool found = _resolved.TryGetValue(target, out ResolvedTarget resolved);
            if (!found)
            {
                resolved = new ResolvedTarget { Target = target };
                _resolvedOrder.Add(target);
            }
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
