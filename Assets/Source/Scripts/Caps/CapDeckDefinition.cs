using UnityEngine;

/// <summary>
/// ScriptableObject asset defining a cap deck — the player's inventory of cap
/// prefabs available to draw into the hand.
///
/// This asset is the READ-ONLY TEMPLATE. The live deck (in <see cref="CapHand"/>)
/// is a runtime copy that gets mutated during play (caps added on win, removed on
/// lose, drawn into hand). On board/level reset, the live deck is restored from
/// this asset.
///
/// Create one via the Asset menu: right-click in Project → Create → Game → Cap Deck.
/// Designers edit the <see cref="Caps"/> array to configure what caps the player
/// starts with. To change the deck between fights, swap the asset reference on
/// <see cref="CapHand.DeckTemplate"/>.
/// </summary>
[CreateAssetMenu(fileName = "NewCapDeck", menuName = "Game/Cap Deck")]
public class CapDeckDefinition : ScriptableObject
{
    [Tooltip("Caps in this deck. The live deck is restored to this list on reset. " +
             "Order matters only when ShuffleOnStart is false — caps are drawn from index 0 first.")]
    public Cap[] Caps;

    [Tooltip("If true, shuffle the deck order when the hand initializes or resets. " +
             "If false, caps are drawn in array order (index 0 first).")]
    public bool ShuffleOnStart = true;
}
