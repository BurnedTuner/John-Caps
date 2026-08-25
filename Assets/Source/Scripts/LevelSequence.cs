using UnityEngine;

/// <summary>
/// ScriptableObject defining the ordered level sequence for a run.
/// Create via: right-click in Project → Create → Game → Level Sequence.
///
/// Assign battle scene names in order, mark boss levels, and assign the
/// starting CapDeckDefinition (the template for the player's initial deck).
/// </summary>
[CreateAssetMenu(fileName = "NewLevelSequence", menuName = "Game/Level Sequence")]
public class LevelSequence : ScriptableObject
{
    [System.Serializable]
    public struct LevelEntry
    {
        [Tooltip("Scene name (as shown in Build Settings).")]
        public string SceneName;

        [Tooltip("If true, this is a boss level — losing does NOT skip forward. " +
                 "The player retries with the deck state from before the attempt.")]
        public bool IsBoss;

        [Tooltip("Optional: the enemy's CapDeckDefinition for this level. " +
                 "If null, the scene's existing OpponentCapPool is used as-is.")]
        public CapDeckDefinition EnemyDeck;
    }

    [Tooltip("Ordered list of levels in the run.")]
    public LevelEntry[] Levels;

    [Tooltip("The player's starting deck template. Caps are copied from this " +
             "at run start, and their visuals are generated once via CapVisualGenerator.")]
    public CapDeckDefinition StartingPlayerDeck;

    [Tooltip("Number of restart hearts the player starts with.")]
    [Min(0)] public int StartingHearts = 3;
}
