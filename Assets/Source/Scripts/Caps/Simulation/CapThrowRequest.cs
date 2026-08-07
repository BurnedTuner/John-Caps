using UnityEngine;

/// <summary>
/// Complete input required by the board simulation to start one throw.
/// </summary>
public readonly struct CapThrowRequest
{
    public Cap Cap { get; }
    public Vector3 StartPosition { get; }
    public Vector3 LandingPosition { get; }
    public float Force { get; }

    public CapThrowRequest(Cap cap, Vector3 startPosition, Vector3 landingPosition, float force)
    {
        Cap = cap;
        StartPosition = startPosition;
        LandingPosition = landingPosition;
        Force = force;
    }
}
