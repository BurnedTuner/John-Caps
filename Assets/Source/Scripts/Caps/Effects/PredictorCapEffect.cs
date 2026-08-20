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
public sealed class PredictorCapEffect : MonoBehaviour, ICapAbility
{
    [Header("Sticker")]
    [Tooltip("Icon sprite shown as a sticker above the cap.")]
    [SerializeField] private Sprite _stickerSprite;

    [Header("Level")]
    [Tooltip("Ability level (1-3). Higher levels = more prediction depth.")]
    [Range(1, 3)] [SerializeField] private int _level = 1;

    [Header("Level Parameters")]
    [Tooltip("Prediction depth bonus at level 1.")]
    [SerializeField] private int _depthBonusL1 = 2;
    [Tooltip("Prediction depth bonus at level 2.")]
    [SerializeField] private int _depthBonusL2 = 4;
    [Tooltip("Prediction depth bonus at level 3.")]
    [SerializeField] private int _depthBonusL3 = 6;

    public Sprite StickerSprite => _stickerSprite;
    public int Level => _level;
    public string Description =>
        $"Когда целишься этой фишкой, видишь на {PredictionDepthBonus} отскока дальше";

    public int PredictionDepthBonus => _level switch { 2 => _depthBonusL2, 3 => _depthBonusL3, _ => _depthBonusL1 };
}
