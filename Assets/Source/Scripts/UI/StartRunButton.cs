using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

/// <summary>
/// Placed on the MAIN MENU scene. Has a single "Start Game" button that
/// creates the RunManager (DontDestroyOnLoad), assigns the LevelSequence,
/// and starts the run. This component is destroyed when the battle scene loads.
///
/// Setup:
/// 1. Add this to any GameObject in the main menu scene.
/// 2. Assign the LevelSequence asset.
/// 3. Assign the "Start Game" button.
/// </summary>
public class StartRunButton : MonoBehaviour
{
    [Header("Config")]
    [Tooltip("The LevelSequence asset that defines the run's levels.")]
    [SerializeField] private LevelSequence _levelSequence;

    [Header("UI")]
    [Tooltip("The 'START GAME' button.")]
    [SerializeField] private Button _startButton;

    void OnEnable()
    {
        if (_startButton != null) _startButton.onClick.AddListener(OnStartGame);
    }

    void OnDisable()
    {
        if (_startButton != null) _startButton.onClick.RemoveListener(OnStartGame);
    }

    void OnStartGame()
    {
        if (_levelSequence == null)
        {
            Debug.LogError("[StartRunButton] No LevelSequence assigned.", this);
            return;
        }

        // Destroy any existing RunManager (from a previous run).
        RunManager existing = FindFirstObjectByType<RunManager>();
        if (existing != null)
            Destroy(existing.gameObject);

        // Create a new RunManager.
        var go = new GameObject("RunManager");
        var runManager = go.AddComponent<RunManager>();
        DontDestroyOnLoad(go);

        // Start the run — this loads the first level scene.
        runManager.StartRun(_levelSequence);
    }
}
