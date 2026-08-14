using UnityEngine;

/// <summary>
/// What caused this cap to be predicted. Used by the prediction-depth system
/// to classify continuation indicators (chain vs stack).
/// </summary>
public enum PredictionSource
{
    /// <summary>Direct hit from the thrown cap.</summary>
    Direct = 0,
    /// <summary>Chain reaction — this cap was hit by another predicted cap landing on it.</summary>
    Chain = 1,
    /// <summary>Stack peel-off — this cap was part of a stack that got hit.</summary>
    Stack = 2,
}

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
    public readonly PredictionSource Source;

    public CapPrediction(
        Cap cap,
        int depth,
        Vector2 startPosition,
        Vector2 direction,
        float force,
        float travelDistance,
        bool willLandHeads = false,
        PredictionSource source = PredictionSource.Direct)
    {
        Cap = cap;
        Depth = depth;
        StartPosition = startPosition;
        Direction = direction;
        Force = force;
        TravelDistance = Mathf.Max(0f, travelDistance);
        WillLandHeads = willLandHeads;
        Source = source;
    }

    public Vector2 EndPosition => StartPosition + Direction * TravelDistance;

    public CapPrediction WithTravelDistance(float travelDistance) =>
        new CapPrediction(Cap, Depth, StartPosition, Direction, Force, travelDistance, WillLandHeads, Source);
}
