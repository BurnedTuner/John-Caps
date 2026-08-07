using UnityEngine;

/// <summary>
/// Immutable data captured at the moment a cap activates its flip effects.
/// </summary>
public readonly struct CapFlipEvent
{
    public Cap Source { get; }
    public Vector2 Position { get; }
    public float IncomingForce { get; }

    public CapFlipEvent(Cap source, Vector2 position, float incomingForce)
    {
        Source = source;
        Position = position;
        IncomingForce = incomingForce;
    }
}
