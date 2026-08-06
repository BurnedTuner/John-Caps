using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Base component for an ability that is activated after this cap finishes a flip.
/// Add a derived component to a cap prefab. A prefab without one is a normal cap.
/// </summary>
public abstract class CapFlipEffect : MonoBehaviour
{
    public abstract void Activate(CapFlipEffectContext context);
}

/// <summary>
/// Runtime access granted to a flip effect by the active turn simulation.
/// Launch requests pass through CapThrower, so chain limits and settling remain intact.
/// </summary>
public sealed class CapFlipEffectContext
{
    private readonly Func<Cap, Vector2, float, bool> _tryLaunch;

    public Cap Source { get; }
    public Vector2 Position { get; }
    public float IncomingForce { get; }
    public IReadOnlyList<Cap> Caps { get; }

    internal CapFlipEffectContext(
        Cap source,
        Vector2 position,
        float incomingForce,
        IReadOnlyList<Cap> caps,
        Func<Cap, Vector2, float, bool> tryLaunch)
    {
        Source = source;
        Position = position;
        IncomingForce = incomingForce;
        Caps = caps;
        _tryLaunch = tryLaunch;
    }

    /// <summary>
    /// Attempts to launch and flip a cap with the requested raw force.
    /// The target cap's PowerConversion and the global motion tuning are applied afterwards.
    /// </summary>
    public bool TryLaunch(Cap target, Vector2 direction, float force)
    {
        if (target == null || target == Source || _tryLaunch == null) return false;
        if (!float.IsFinite(force) || force <= 0f) return false;
        if (!float.IsFinite(direction.x) || !float.IsFinite(direction.y)) return false;

        return _tryLaunch(target, direction, force);
    }
}
