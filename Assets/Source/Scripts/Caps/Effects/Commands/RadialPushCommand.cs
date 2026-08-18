using UnityEngine;

/// <summary>
/// Requests an equal push force for available caps inside a circular area.
/// Unlike RadialLaunchCommand, this PUSHES caps (slides them without flipping)
/// rather than launching them (which flips). Used by the bomb rework.
/// </summary>
public sealed class RadialPushCommand : ICapEffectCommand
{
    public Cap Source { get; }
    public Vector2 Origin { get; }
    public float Radius { get; }
    public float Force { get; }

    public RadialPushCommand(Cap source, Vector2 origin, float radius, float force)
    {
        Source = source;
        Origin = origin;
        Radius = radius;
        Force = force;
    }
}
