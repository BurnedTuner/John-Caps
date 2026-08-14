using UnityEngine;

/// <summary>
/// Grants a prediction-depth bonus while the player is aiming with this cap.
/// When this cap is the held cap (being aimed), the player's effective
/// PredictionDepth is increased by <see cref="PredictionDepthBonus"/>.
///
/// The bonus applies only during aim preview — it does not affect the actual
/// throw, chain reactions, or the AI. It's purely a player-aim-assist buff.
///
/// Add this component to a cap prefab. A prefab without one is a normal cap
/// (no bonus).
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Cap))]
public sealed class PredictorCapEffect : MonoBehaviour
{
    [Tooltip("Extra prediction depth levels granted while aiming with this cap. " +
             "0 = no bonus (same as not having the component). " +
             "2 = the player sees 2 more flip trajectories than usual.")]
    [Min(0)] public int PredictionDepthBonus = 2;
}
