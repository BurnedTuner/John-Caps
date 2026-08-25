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

        [Tooltip("If true (default), scene-placed enemy caps in this scene are " +
                 "replaced by random caps drawn from EnemyDeck when the level loads. " +
                 "The drawn caps are depleted from the enemy deck (so the AI can't " +
                 "throw them again). Lets designers compose enemy layouts in the scene " +
                 "editor without picking which specific caps they are — the run picks " +
                 "random ones from the deck at load time.")]
        public bool ReplaceSceneEnemyCapsOnLoad;

        [Tooltip("Random position offset (in world units) applied to each replaced " +
                 "enemy cap when ReplaceSceneEnemyCapsOnLoad is active. The replacement " +
                 "cap is placed at a random point within this radius of the original " +
                 "scene-placed cap's position. 0 = exact original position.")]
        [Min(0f)] public float ReplacementPositionJitter;
    }

    [Tooltip("Ordered list of levels in the run.")]
    public LevelEntry[] Levels;

    [Tooltip("The player's starting deck template. Caps are copied from this " +
             "at run start, and their visuals are generated once via CapVisualGenerator.")]
    public CapDeckDefinition StartingPlayerDeck;

    [Tooltip("Number of restart hearts the player starts with.")]
    [Min(0)] public int StartingHearts = 3;
}
