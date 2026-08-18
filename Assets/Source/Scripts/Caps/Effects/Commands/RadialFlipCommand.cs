using UnityEngine;

/// <summary>
/// Requests that caps inside a circular area flip IN PLACE — they flip (toggle
/// IsHeads, play the flip animation) but do NOT move. Stacks flip as a unit
/// (all caps flip, no peel-off). Used by the FlipperCapEffect.
/// </summary>
public sealed class RadialFlipCommand : ICapEffectCommand
{
    public Cap Source { get; }
    public Vector2 Origin { get; }
    public float Radius { get; }

    public RadialFlipCommand(Cap source, Vector2 origin, float radius)
    {
        Source = source;
        Origin = origin;
        Radius = radius;
    }
}
