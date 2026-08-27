using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;

/// <summary>
/// Placed in each BATTLE scene. Shows post-battle panels (win, lose-skip,
/// lose-boss, run-over, victory), hearts, and level text. Auto-finds
/// RunManager on Awake and subscribes to its events. Destroyed when the
/// next scene loads.
///
/// IMPORTANT — flow change:
///   Navigation buttons no longer call RunManager.AdvanceToNextLevel /
///   RestartCurrentLevel directly. Instead, they load the RunProgress scene
///   (an intermediate hub between battles). The RunProgress scene reads
///   LastBattleResult to show the gained/lost caps, then its own "Next Level"
///   button calls AdvanceToNextLevel / RestartCurrentLevel.
///
///   This component does NOT clear LastBattleResult — the RunProgress scene
///   owns the lifecycle now. Clearing it here would prevent the progress scene
///   from displaying the gained/lost caps.
///
///   The match-rewards panel (gained/lost caps with stickers) is NO LONGER
///   shown in the battle scene. It's only shown in the RunProgress scene.
///
/// Setup (per battle scene):
/// 1. Add this to a GameObject in the battle scene.
/// 2. Assign panels: _winPanel, _loseSkipPanel, _loseBossPanel, _runOverPanel, _runVictoryPanel.
///    Set all panels inactive by default.
/// 3. Assign buttons on each panel: _nextLevelButton, _loseContinueButton, _bossRetryButton, _returnToMenuButton, _victoryReturnButton.
/// 4. Assign heart images (3 Images — disabled left-to-right as hearts are lost).
/// 5. (Optional) Assign _levelText, _mainMenuSceneName, _progressSceneName.
/// </summary>
public class BattleResultUI : MonoBehaviour
{
    [Header("Post-battle panels")]
    [Tooltip("Panel shown when the player wins a battle.")]
    [SerializeField] private GameObject _winPanel;

    [Tooltip("Button on the win panel to go to the next level.")]
    [SerializeField] private Button _nextLevelButton;

    [Tooltip("Panel shown when the player loses a non-boss battle (skip to next).")]
    [SerializeField] private GameObject _loseSkipPanel;

    [Tooltip("Button on the lose-skip panel to continue to the next level.")]
    [SerializeField] private Button _loseContinueButton;

    [Tooltip("Panel shown when the player loses a boss battle (retry).")]
    [SerializeField] private GameObject _loseBossPanel;

    [Tooltip("Button on the lose-boss panel to retry the boss.")]
    [SerializeField] private Button _bossRetryButton;

    [Tooltip("Panel shown when the run is over (no hearts left).")]
    [SerializeField] private GameObject _runOverPanel;

    [Tooltip("Button on the run-over panel to return to the main menu.")]
    [SerializeField] private Button _returnToMenuButton;

    [Tooltip("Button on the run-over panel to restart the run from level 1 with a fresh deck.")]
    [SerializeField] private Button _restartRunButton;

    [Tooltip("Panel shown when the player clears all levels (victory).")]
    [SerializeField] private GameObject _runVictoryPanel;

    [Tooltip("Button on the victory panel to return to the main menu.")]
    [SerializeField] private Button _victoryReturnButton;

    [Header("Hearts UI")]
    [Tooltip("Heart images — one per starting heart. Disabled left-to-right as hearts are lost.")]
    [SerializeField] private Image[] _heartImages;

    [Header("Level display")]
    [Tooltip("Optional text showing the current level number.")]
    [SerializeField] private TMP_Text _levelText;

    [Header("Scene config")]
    [Tooltip("The RunProgress scene name (loaded after the player dismisses the battle result panel).")]
    [SerializeField] private string _progressSceneName = "RunProgress";

    [Tooltip("The main menu scene name (loaded when the run ends).")]
    [SerializeField] private string _mainMenuSceneName = "MainMenu";

    [Header("Fall animation wait")]
    [Tooltip("If true, the win/lose panel waits until all FallingCap components " +
             "have finished (caps dropped below vanish height or timed out) before " +
             "showing. Lets the player see the final caps fall off the table.")]
    [SerializeField] private bool _waitForFallAnimation = true;

    [Tooltip("Maximum seconds to wait for fall animations to finish before showing " +
             "the panel anyway. Safety net in case a FallingCap gets stuck " +
             "(e.g., a cap that never reaches the vanish height).")]
    [Min(0.1f)] [SerializeField] private float _fallWaitTimeoutSeconds = 12f;

    private RunManager _runManager;

    // Stashed battle result + timestamp — used to defer showing the panel until
    // all FallingCap components have finished. Set when HandleBattleEnded fires;
    // consumed in Update.
    private BattleResult _pendingResult;
    private float _pendingResultTime;
    private bool _isWaitingForFall;

    void Awake()
    {
        _runManager = RunManager.Instance;
    }

    void OnEnable()
    {
        WireButtons();
        SubscribeToRunManager();
    }

    void OnDisable()
    {
        UnwireButtons();
        UnsubscribeFromRunManager();
    }

    void Start()
    {
        HideAllPanels();
        UpdateHearts();
        UpdateLevelText();
    }

    void Update()
    {
        // If we're waiting for fall animations to finish, check every frame
        // whether any FallingCap components are still active. If none (or the
        // timeout expired), show the stashed panel.
        if (_isWaitingForFall)
        {
            bool timedOut = Time.time - _pendingResultTime >= _fallWaitTimeoutSeconds;
            bool anyFalling = AreAnyCapsFalling();

            if (!anyFalling || timedOut)
            {
                _isWaitingForFall = false;
                ShowPendingPanel();
            }
        }
    }

    /// <summary>
    /// Returns true if any FallingCap component is currently active in the scene.
    /// Used to wait for fall animations to finish before showing the win/lose panel.
    /// </summary>
    static bool AreAnyCapsFalling()
    {
        // FallingCap is a MonoBehaviour added to caps when they leave the field.
        // We use FindFirstObjectByType (Unity 2023+) which is allocation-free
        // for the "any" check. If multiple caps are falling, only the first is
        // returned, but we only need to know "is at least one falling".
        var falling = FindFirstObjectByType<FallingCap>();
        return falling != null;
    }

    // -----------------------------------------------------------------------
    // Button wiring
    // -----------------------------------------------------------------------

    void WireButtons()
    {
        if (_nextLevelButton != null) _nextLevelButton.onClick.AddListener(OnNextLevel);
        if (_loseContinueButton != null) _loseContinueButton.onClick.AddListener(OnLoseContinue);
        if (_bossRetryButton != null) _bossRetryButton.onClick.AddListener(OnBossRetry);
        if (_returnToMenuButton != null) _returnToMenuButton.onClick.AddListener(OnReturnToMenu);
        if (_victoryReturnButton != null) _victoryReturnButton.onClick.AddListener(OnReturnToMenu);
        if (_restartRunButton != null) _restartRunButton.onClick.AddListener(OnRestartRun);
    }

    void UnwireButtons()
    {
        if (_nextLevelButton != null) _nextLevelButton.onClick.RemoveListener(OnNextLevel);
        if (_loseContinueButton != null) _loseContinueButton.onClick.RemoveListener(OnLoseContinue);
        if (_bossRetryButton != null) _bossRetryButton.onClick.RemoveListener(OnBossRetry);
        if (_returnToMenuButton != null) _returnToMenuButton.onClick.RemoveListener(OnReturnToMenu);
        if (_victoryReturnButton != null) _victoryReturnButton.onClick.RemoveListener(OnReturnToMenu);
        if (_restartRunButton != null) _restartRunButton.onClick.RemoveListener(OnRestartRun);
    }

    // -----------------------------------------------------------------------
    // RunManager subscription
    // -----------------------------------------------------------------------

    void SubscribeToRunManager()
    {
        if (_runManager == null) _runManager = RunManager.Instance;
        if (_runManager != null)
        {
            _runManager.OnBattleEnded += HandleBattleEnded;
            _runManager.OnHeartsChanged += HandleHeartsChanged;
            _runManager.OnRunEnded += HandleRunEnded;
        }
    }

    void UnsubscribeFromRunManager()
    {
        if (_runManager != null)
        {
            _runManager.OnBattleEnded -= HandleBattleEnded;
            _runManager.OnHeartsChanged -= HandleHeartsChanged;
            _runManager.OnRunEnded -= HandleRunEnded;
        }
    }

    // -----------------------------------------------------------------------
    // Button handlers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Loads the RunProgress scene. The progress scene reads LastBattleResult
    /// to show the gained/lost caps and decides which navigation button to show
    /// (Next Level / Retry Boss / Return to Menu).
    ///
    /// Does NOT clear LastBattleResult — the progress scene owns the lifecycle
    /// now, and it needs LastBattleResult to populate the rewards panel.
    /// </summary>
    void LoadProgressScene()
    {
        HideAllPanels();
        if (!string.IsNullOrEmpty(_progressSceneName))
            SceneManager.LoadScene(_progressSceneName);
    }

    void OnNextLevel()
    {
        // Win: go to the progress scene, which shows the next-level button.
        LoadProgressScene();
    }

    void OnLoseContinue()
    {
        // Non-boss loss: go to the progress scene, which shows the next-level button (skip).
        LoadProgressScene();
    }

    void OnBossRetry()
    {
        // Boss loss: go to the progress scene, which shows the retry-boss button.
        LoadProgressScene();
    }

    void OnReturnToMenu()
    {
        // Run over / victory: clear LastBattleResult and return to the main menu.
        HideAllPanels();
        if (_runManager != null)
            _runManager.ClearLastBattleResult();
        if (!string.IsNullOrEmpty(_mainMenuSceneName))
            SceneManager.LoadScene(_mainMenuSceneName);
    }

    /// <summary>
    /// Called by the "Restart Run" button on the game over panel. Restarts
    /// the run from level 1 with a fresh deck + full hearts.
    /// </summary>
    void OnRestartRun()
    {
        HideAllPanels();
        if (_runManager != null)
            _runManager.RestartRun();
    }

    // -----------------------------------------------------------------------
    // Event handlers
    // -----------------------------------------------------------------------

    void HandleBattleEnded(BattleResult result)
    {
        if (_runManager == null || result == null) return;

        UpdateLevelText();

        // Stash the result. If we're waiting for fall animations, the panel
        // shows once no FallingCap components remain (or the timeout expires).
        // Otherwise, show immediately.
        _pendingResult = result;
        _pendingResultTime = Time.time;

        if (_waitForFallAnimation && AreAnyCapsFalling())
        {
            // Defer the panel show until the fall animations finish.
            _isWaitingForFall = true;
        }
        else
        {
            // No falling caps (or wait disabled) — show the panel now.
            _isWaitingForFall = false;
            ShowPendingPanel();
        }
    }

    /// <summary>
    /// Shows the appropriate panel for the stashed _pendingResult. Called either
    /// immediately (if no fall animations are running) or after the wait completes.
    /// </summary>
    void ShowPendingPanel()
    {
        if (_pendingResult == null) return;
        BattleResult result = _pendingResult;
        _pendingResult = null;

        bool playerWon = result.PlayerWon;

        if (playerWon)
        {
            if (result.WasLastLevel)
                ShowPanel(_runVictoryPanel);
            else
                ShowPanel(_winPanel);
        }
        else
        {
            if (_runManager != null && _runManager.Hearts <= 0)
                ShowPanel(_runOverPanel);
            else if (result.IsBoss)
                ShowPanel(_loseBossPanel);
            else
                ShowPanel(_loseSkipPanel);
        }
    }

    void HandleHeartsChanged(int newHearts)
    {
        UpdateHearts(newHearts);
    }

    void HandleRunEnded(bool isVictory)
    {
        // The run has ended (victory or run-over). This fires AFTER HandleBattleEnded
        // (RunManager.OnMatchFinished calls OnBattleEnded then EndRun → OnRunEnded).
        // HandleBattleEnded already stashed the result and started the wait (if any
        // FallingCap components are still active). When the wait completes,
        // ShowPendingPanel will show the correct panel based on the stashed result.
        //
        // If HandleBattleEnded decided to show immediately (no falling caps), the
        // panel is already showing. In that case, we don't need to do anything here
        // — ShowPendingPanel already chose _runVictoryPanel or _runOverPanel based
        // on result.WasLastLevel / result.PlayerWon / Hearts.
        //
        // If we're still waiting for fall animations, the pending result will be
        // shown when the wait completes. Don't override it here.
        //
        // If there's no pending result (HandleBattleEnded already showed the panel),
        // we don't need to re-show — the correct panel is already up.
        if (_isWaitingForFall) return;
        // Defensive: if there's no pending result AND no panel is currently shown,
        // show the appropriate run-end panel. This handles edge cases where
        // HandleBattleEnded didn't fire for some reason.
        bool anyPanelActive = (_runVictoryPanel != null && _runVictoryPanel.activeSelf)
                              || (_runOverPanel != null && _runOverPanel.activeSelf)
                              || (_winPanel != null && _winPanel.activeSelf)
                              || (_loseSkipPanel != null && _loseSkipPanel.activeSelf)
                              || (_loseBossPanel != null && _loseBossPanel.activeSelf);
        if (!anyPanelActive)
        {
            if (isVictory)
                ShowPanel(_runVictoryPanel);
            else
                ShowPanel(_runOverPanel);
        }
    }

    // -----------------------------------------------------------------------
    // UI helpers
    // -----------------------------------------------------------------------

    void ShowPanel(GameObject panel)
    {
        HideAllPanels();
        if (panel != null) panel.SetActive(true);
    }

    void HideAllPanels()
    {
        if (_winPanel != null) _winPanel.SetActive(false);
        if (_loseSkipPanel != null) _loseSkipPanel.SetActive(false);
        if (_loseBossPanel != null) _loseBossPanel.SetActive(false);
        if (_runOverPanel != null) _runOverPanel.SetActive(false);
        if (_runVictoryPanel != null) _runVictoryPanel.SetActive(false);
    }

    void UpdateHearts()
    {
        if (_runManager != null)
            UpdateHearts(_runManager.Hearts);
    }

    void UpdateHearts(int hearts)
    {
        if (_heartImages == null) return;
        for (int i = 0; i < _heartImages.Length; i++)
        {
            if (_heartImages[i] != null)
                _heartImages[i].gameObject.SetActive(i < hearts);
        }
    }

    void UpdateLevelText()
    {
        if (_levelText == null || _runManager == null) return;
        int displayLevel = _runManager.CurrentLevelIndex + 1;
        _levelText.text = $"Level {displayLevel} / {_runManager.TotalLevels}";
    }
}
