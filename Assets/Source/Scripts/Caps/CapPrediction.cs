using UnityEngine;

/// <summary>
/// Immutable description of what will happen to one cap during a throw.
/// </summary>
public readonly struct CapPrediction
{
    public readonly Cap Cap;
    public readonly int Depth;
    public readonly Vector2 StartPosition;
    public readonly Vector2 Direction;
    public readonly float Force;
    public readonly float TravelDistance;
    public readonly bool WillLandHeads;

    public CapPrediction(
        Cap cap,
        int depth,
        Vector2 startPosition,
        Vector2 direction,
        float force,
        float travelDistance,
        bool willLandHeads = false)
    {
        Cap = cap;
        Depth = depth;
        StartPosition = startPosition;
        Direction = direction;
        Force = force;
        TravelDistance = Mathf.Max(0f, travelDistance);
        WillLandHeads = willLandHeads;
    }

    public Vector2 EndPosition => StartPosition + Direction * TravelDistance;

    public CapPrediction WithTravelDistance(float travelDistance) =>
        new CapPrediction(Cap, Depth, StartPosition, Direction, Force, travelDistance, WillLandHeads);
}
