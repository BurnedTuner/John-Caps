using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Builds and applies effect commands immediately at a safe point in the current simulation step.
/// </summary>
internal sealed class CapEffectResolver
{
    private readonly ICapEffectQuery _query;
    private readonly ICapEffectCommandExecutor _executor;
    private readonly CapEffectCommandBuffer _commandBuffer = new();
    private readonly List<Cap> _targets = new();

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
            Execute(commands[i]);

        _commandBuffer.Clear();
    }

    void Execute(ICapEffectCommand command)
    {
        if (command is RadialLaunchCommand radialLaunch)
            ExecuteRadialLaunch(radialLaunch);
        else
            Debug.LogError($"[CapEffectResolver] Unsupported command: {command?.GetType().Name ?? "null"}.");
    }

    void ExecuteRadialLaunch(RadialLaunchCommand command)
    {
        if (command.Source == null || command.Radius <= 0f || command.Force <= 0f)
            return;

        _query.CollectCapsInRadius(command.Origin, command.Radius, _targets);

        for (int i = 0; i < _targets.Count; i++)
        {
            Cap target = _targets[i];
            if (target == null || target == command.Source || target.IsBusy) continue;

            Vector2 offset = target.GroundPosition - command.Origin;
            Vector2 direction = offset.sqrMagnitude > 0.000001f
                ? offset.normalized
                : Vector2.right;

            _executor.TryLaunch(command.Source, target, direction, command.Force);
        }

        _targets.Clear();
    }
}
