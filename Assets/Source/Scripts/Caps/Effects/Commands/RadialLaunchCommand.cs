using UnityEngine;

/// <summary>
/// Requests an equal launch force for available caps inside a circular area.
/// </summary>
public sealed class RadialLaunchCommand : ICapEffectCommand
{
    public Cap Source { get; }
    public Vector2 Origin { get; }
    public float Radius { get; }
    public float Force { get; }

    public RadialLaunchCommand(Cap source, Vector2 origin, float radius, float force)
    {
        Source = source;
        Origin = origin;
        Radius = radius;
        Force = force;
    }
}
