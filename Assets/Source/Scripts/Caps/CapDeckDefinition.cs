using UnityEngine;

/// <summary>
/// ScriptableObject asset defining a cap deck — the player's or opponent's
/// inventory of caps. Each entry is a COMPOSED cap: a base visual prefab +
/// per-ability level sliders (0-3, where 0 means no ability).
///
/// Ability PARAMETERS (radius, force, VFX, trigger side, etc.) come from
/// ABILITY TEMPLATE PREFABS — assigned once per deck asset. At runtime,
/// CapFactory.CreateComposed instantiates the base prefab, then for each
/// non-zero ability level, adds the corresponding ability component to the
/// instance and copies its parameters from the template, then sets the level.
///
/// This replaces the old CapDeckDefinition (which was just Cap[] — abilities
/// were baked into each prefab). The new structure lets designers compose
/// decks with different ability combinations without creating a prefab for
/// every variant.
///
/// Create via: right-click in Project → Create → Game → Cap Deck.
/// </summary>
[CreateAssetMenu(fileName = "NewCapDeck", menuName = "Game/Cap Deck")]
public class CapDeckDefinition : ScriptableObject
{
    [System.Serializable]
    public struct ComposedCapEntry
    {
        [Tooltip("Base cap prefab — visual only (face/back/rim materials, CapVisualGenerator). " +
                 "Should have NO ability components (Bomb/Flipper/Defender/Predictor). " +
                 "Abilities are added at runtime based on the level sliders below.")]
        public Cap BasePrefab;

        [Tooltip("Bomb ability level. 0 = no bomb. 1-3 = bomb with that level's parameters.")]
        [Range(0, 3)] public int BombLevel;

        [Tooltip("Flipper ability level. 0 = no flipper. 1-3 = flipper with that level's parameters.")]
        [Range(0, 3)] public int FlipperLevel;

        [Tooltip("Defender ability level. 0 = no defender. 1-3 = defender with that level's parameters.")]
        [Range(0, 3)] public int DefenderLevel;

        [Tooltip("Predictor ability level. 0 = no predictor. 1-3 = predictor with that level's parameters.")]
        [Range(0, 3)] public int PredictorLevel;
    }

    [Header("Ability templates")]
    [Tooltip("Cap prefab with BombCapFlipEffect configured (sticker, L1/L2/L3 radius+force, trigger side, VFX). " +
             "Used as the parameter source when a cap entry has BombLevel > 0.")]
    [SerializeField] private Cap _bombTemplate;

    [Tooltip("Cap prefab with FlipperCapEffect configured.")]
    [SerializeField] private Cap _flipperTemplate;

    [Tooltip("Cap prefab with DefenderCapEffect configured.")]
    [SerializeField] private Cap _defenderTemplate;

    [Tooltip("Cap prefab with PredictorCapEffect configured.")]
    [SerializeField] private Cap _predictorTemplate;

    [Header("Entries")]
    [Tooltip("Caps in this deck. Each entry composes a base prefab + ability levels. " +
             "Order matters only when ShuffleOnStart is false — caps are drawn from index 0 first.")]
    public ComposedCapEntry[] Caps;

    [Tooltip("If true, shuffle the deck order when the hand initializes or resets. " +
             "If false, caps are drawn in array order (index 0 first).")]
    public bool ShuffleOnStart = true;

    /// <summary>Template prefab for the bomb ability. Null if not configured.</summary>
    public Cap BombTemplate => _bombTemplate;
    /// <summary>Template prefab for the flipper ability. Null if not configured.</summary>
    public Cap FlipperTemplate => _flipperTemplate;
    /// <summary>Template prefab for the defender ability. Null if not configured.</summary>
    public Cap DefenderTemplate => _defenderTemplate;
    /// <summary>Template prefab for the predictor ability. Null if not configured.</summary>
    public Cap PredictorTemplate => _predictorTemplate;
}
