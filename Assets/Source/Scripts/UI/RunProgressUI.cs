using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;

/// <summary>
/// Host script for the RUN PROGRESS scene — an intermediate "hub" shown
/// between battles. Reads RunManager state and builds:
///   - A horizontal progress bar with one node per level (boss levels marked).
///   - A current-position indicator (chevron) above the active node.
///   - Hearts display (same pattern as BattleResultUI).
///   - A deck button + pop-out panel (same logic as battle DeckPanelUI, but
///     reading from RunManager.RunDeck via DeckPanelUI.Source.RunManager).
///   - The last battle's rewards (gained/lost caps) via MatchRewardsPanel.
///   - A context-appropriate navigation button:
///       Won (not last)  → "Next Level"  → AdvanceToNextLevel()
///       Won (last)      → "Run Complete" → return to menu
///       Lost non-boss   → "Next Level"  → AdvanceToNextLevel() (skip)
///       Lost boss       → "Retry Boss"  → RestartCurrentLevel()
///       Lost (0 hearts) → "Run Over"    → return to menu
///
/// The previous battle scene's BattleResultUI loaded this scene WITHOUT
/// clearing LastBattleResult, so this script can read it here.
///
/// Setup (per RunProgress scene):
/// 1. Add this to a GameObject in the RunProgress scene's Canvas.
/// 2. Assign _progressBarParent (RectTransform with a HorizontalLayoutGroup).
/// 3. Assign _levelNodePrefab (root has an Image; optional child Text for number).
/// 4. Assign _currentIndicator (a chevron/arrow RectTransform, child of the bar).
/// 5. Assign _completedNodeColor, _currentNodeColor, _futureNodeColor, _bossNodeColor.
/// 6. Assign _heartImages (3 Images — disabled left-to-right as hearts are lost).
/// 7. Assign _nextLevelButton, _retryBossButton, _returnToMenuButton.
///    Set _retryBossButton and _returnToMenuButton inactive by default — this
///    script activates the appropriate one based on LastBattleResult.
/// 8. Assign _rewardsPanel (MatchRewardsPanel reference).
/// 9. Assign _deckPanel (DeckPanelUI reference — set its Source to RunManager
///    in its own inspector).
/// 10. (Optional) Assign _levelText, _statusText, _mainMenuSceneName.
/// </summary>
public class RunProgressUI : MonoBehaviour
{
    [Header("Progress bar")]
    [Tooltip("Parent RectTransform where level nodes are instantiated. " +
             "Should have a HorizontalLayoutGroup so nodes are evenly spaced.")]
    [SerializeField] private RectTransform _progressBarParent;

    [Tooltip("Prefab for one level node. Root should have an Image. " +
             "Optional child TMP_Text named 'Number' will show the level number. " +
             "Optional child Image named 'BossIcon' will be enabled for boss levels.")]
    [SerializeField] private GameObject _levelNodePrefab;

    [Tooltip("The chevron/arrow RectTransform that indicates the player's current position. " +
             "Should be a child of _progressBarParent (positioned above the active node).")]
    [SerializeField] private RectTransform _currentIndicator;

    [Header("Node colors")]
    [SerializeField] private Color _completedNodeColor = new Color(0.2f, 0.8f, 0.3f, 1f);
    [SerializeField] private Color _currentNodeColor = new Color(1f, 0.85f, 0.2f, 1f);
    [SerializeField] private Color _futureNodeColor = new Color(0.4f, 0.4f, 0.4f, 0.5f);
    [SerializeField] private Color _bossNodeColor = new Color(0.9f, 0.2f, 0.2f, 1f);

    [Header("Hearts")]
    [Tooltip("Heart images — one per starting heart. Disabled left-to-right as hearts are lost.")]
    [SerializeField] private Image[] _heartImages;

    [Header("Navigation buttons")]
    [Tooltip("Button to advance to the next level (or skip after a non-boss loss).")]
    [SerializeField] private Button _nextLevelButton;

    [Tooltip("Button to retry the current boss level. Set inactive by default.")]
    [SerializeField] private Button _retryBossButton;

    [Tooltip("Button to return to the main menu (shown when the run is over). Set inactive by default.")]
    [SerializeField] private Button _returnToMenuButton;

    [Tooltip("Button to restart the run from level 1 with a fresh deck (shown when the run is over). Set inactive by default.")]
    [SerializeField] private Button _restartRunButton;

    [Header("Rewards + deck")]
    [Tooltip("The match-rewards panel that shows caps gained/lost in the last battle.")]
    [SerializeField] private MatchRewardsPanel _rewardsPanel;

    [Tooltip("The deck panel UI (set its Source to RunManager in its own inspector).")]
    [SerializeField] private DeckPanelUI _deckPanel;

    [Header("Texts (optional)")]
    [Tooltip("Optional text showing the current level number (e.g., 'Level 3 / 5').")]
    [SerializeField] private TMP_Text _levelText;

    [Tooltip("Optional status text shown above the navigation button " +
             "(e.g., 'Victory!', 'Defeated — retry?', 'Run Over').")]
    [SerializeField] private TMP_Text _statusText;

    [Header("Scene config")]
    [Tooltip("The main menu scene name (loaded when the run ends or the player returns).")]
    [SerializeField] private string _mainMenuSceneName = "MainMenu";

    [Header("Settings panel (optional)")]
    [Tooltip("Button that toggles the settings panel open/closed.")]
    [SerializeField] private Button _settingsButton;

    [Tooltip("The settings panel GameObject. Starts inactive. Should contain SettingsBinder + SFX/BGM sliders.")]
    [SerializeField] private GameObject _settingsPanel;

    [Tooltip("Button inside the settings panel that closes it.")]
    [SerializeField] private Button _settingsBackButton;

    [Header("Leave to menu")]
    [Tooltip("Button that returns to the main menu at any time (always visible, not just on run over).")]
    [SerializeField] private Button _leaveToMenuButton;

    private RunManager _runManager;
    private readonly List<GameObject> _spawnedNodes = new();

    void Awake()
    {
        _runManager = RunManager.Instance;
    }

    void OnEnable()
    {
        WireButtons();
    }

    void OnDisable()
    {
        UnwireButtons();
    }

    void Start()
    {
        if (_runManager == null)
        {
            Debug.LogError("[RunProgressUI] No RunManager found. Is the run active?", this);
            return;
        }

        BuildProgressBar();
        UpdateHearts();
        UpdateLevelText();
        PopulateRewards();
        ShowContextButton();

        // Settings panel starts hidden.
        if (_settingsPanel != null)
            _settingsPanel.SetActive(false);
    }

    // -----------------------------------------------------------------------
    // Button wiring
    // -----------------------------------------------------------------------

    void WireButtons()
    {
        if (_nextLevelButton != null) _nextLevelButton.onClick.AddListener(OnNextLevel);
        if (_retryBossButton != null) _retryBossButton.onClick.AddListener(OnRetryBoss);
        if (_returnToMenuButton != null) _returnToMenuButton.onClick.AddListener(OnReturnToMenu);
        if (_restartRunButton != null) _restartRunButton.onClick.AddListener(OnRestartRun);
        if (_settingsButton != null) _settingsButton.onClick.AddListener(OnToggleSettings);
        if (_settingsBackButton != null) _settingsBackButton.onClick.AddListener(OnCloseSettings);
        if (_leaveToMenuButton != null) _leaveToMenuButton.onClick.AddListener(OnLeaveToMenu);
    }

    void UnwireButtons()
    {
        if (_nextLevelButton != null) _nextLevelButton.onClick.RemoveListener(OnNextLevel);
        if (_retryBossButton != null) _retryBossButton.onClick.RemoveListener(OnRetryBoss);
        if (_returnToMenuButton != null) _returnToMenuButton.onClick.RemoveListener(OnReturnToMenu);
        if (_restartRunButton != null) _restartRunButton.onClick.RemoveListener(OnRestartRun);
        if (_settingsButton != null) _settingsButton.onClick.RemoveListener(OnToggleSettings);
        if (_settingsBackButton != null) _settingsBackButton.onClick.RemoveListener(OnCloseSettings);
        if (_leaveToMenuButton != null) _leaveToMenuButton.onClick.RemoveListener(OnLeaveToMenu);
    }

    // -----------------------------------------------------------------------
    // Navigation handlers
    // -----------------------------------------------------------------------

    void OnNextLevel()
    {
        if (_runManager == null) return;
        // Clear LastBattleResult BEFORE advancing — the next battle scene's
        // MatchRewardsPanel must not see a stale result. (This was previously
        // done in BattleResultUI, but now the progress scene owns the lifecycle.)
        _runManager.ClearLastBattleResult();

        // Hide the rewards panel so it doesn't briefly show stale data on the
        // next progress scene (if the next battle is also won/lost).
        if (_rewardsPanel != null) _rewardsPanel.Hide();

        _runManager.AdvanceToNextLevel();
    }

    void OnRetryBoss()
    {
        if (_runManager == null) return;
        _runManager.ClearLastBattleResult();
        if (_rewardsPanel != null) _rewardsPanel.Hide();
        _runManager.RestartCurrentLevel();
    }

    void OnReturnToMenu()
    {
        if (_runManager == null) return;
        _runManager.ClearLastBattleResult();
        if (_rewardsPanel != null) _rewardsPanel.Hide();
        if (!string.IsNullOrEmpty(_mainMenuSceneName))
            SceneManager.LoadScene(_mainMenuSceneName);
    }

    /// <summary>
    /// Called by the "Restart Run" button. Restarts the run from level 1
    /// with a fresh deck + full hearts.
    /// </summary>
    void OnRestartRun()
    {
        if (_runManager == null) return;
        _runManager.ClearLastBattleResult();
        if (_rewardsPanel != null) _rewardsPanel.Hide();
        _runManager.RestartRun();
    }

    /// <summary>Toggles the settings panel open/closed.</summary>
    void OnToggleSettings()
    {
        if (_settingsPanel != null)
        {
            bool newState = !_settingsPanel.activeSelf;
            _settingsPanel.SetActive(newState);
            UIBlockState.SetSettingsPanelOpen(newState);
        }
    }

    /// <summary>Closes the settings panel.</summary>
    void OnCloseSettings()
    {
        if (_settingsPanel != null)
            _settingsPanel.SetActive(false);
        UIBlockState.SetSettingsPanelOpen(false);
    }

    /// <summary>
    /// Returns to the main menu at any time (not just on run over). Clears
    /// the battle result + hides rewards.
    /// </summary>
    void OnLeaveToMenu()
    {
        if (_runManager != null)
            _runManager.ClearLastBattleResult();
        if (_rewardsPanel != null)
            _rewardsPanel.Hide();
        if (!string.IsNullOrEmpty(_mainMenuSceneName))
            SceneManager.LoadScene(_mainMenuSceneName);
    }

    // -----------------------------------------------------------------------
    // Progress bar
    // -----------------------------------------------------------------------

    /// <summary>
    /// The level index the player is ABOUT TO PLAY (i.e., the next level after
    /// a win, or the current level after a boss loss / non-boss loss).
    ///
    /// - After a WIN (not last level): the player advances, so the indicator
    ///   points at CurrentLevelIndex + 1.
    /// - After a WIN (last level): the run is complete — indicator past the end.
    /// - After a non-boss LOSS: the player skips forward, so CurrentLevelIndex
    ///   is still the level just lost (RunManager hasn't advanced yet). The
    ///   next level is CurrentLevelIndex + 1.
    /// - After a boss LOSS: the player retries, so the indicator stays at
    ///   CurrentLevelIndex.
    /// - After a 0-hearts LOSS (run over): indicator stays at CurrentLevelIndex.
    /// </summary>
    int IndicatorLevelIndex
    {
        get
        {
            if (_runManager == null || _runManager.LastBattleResult == null)
                return _runManager != null ? _runManager.CurrentLevelIndex : 0;

            var result = _runManager.LastBattleResult;
            if (result.PlayerWon)
                return _runManager.CurrentLevelIndex + 1; // advance
            // Lost.
            if (_runManager.Hearts <= 0) return _runManager.CurrentLevelIndex; // run over
            if (result.IsBoss) return _runManager.CurrentLevelIndex; // retry
            return _runManager.CurrentLevelIndex + 1; // skip forward
        }
    }

    void BuildProgressBar()
    {
        if (_progressBarParent == null || _levelNodePrefab == null || _runManager == null) return;

        // Clear any old nodes (e.g., if the scene is reloaded).
        for (int i = 0; i < _spawnedNodes.Count; i++)
        {
            if (_spawnedNodes[i] != null) Destroy(_spawnedNodes[i]);
        }
        _spawnedNodes.Clear();

        int total = _runManager.TotalLevels;
        int indicatorIndex = IndicatorLevelIndex;

        for (int i = 0; i < total; i++)
        {
            GameObject nodeObj = Instantiate(_levelNodePrefab, _progressBarParent);
            nodeObj.name = $"LevelNode_{i + 1}";
            _spawnedNodes.Add(nodeObj);

            Image nodeImage = nodeObj.GetComponent<Image>();
            if (nodeImage == null) nodeImage = nodeObj.GetComponentInChildren<Image>();

            // Determine the node's color.
            Color color;
            if (i < indicatorIndex)
                color = _completedNodeColor;
            else if (i == indicatorIndex)
                color = _currentNodeColor;
            else
                color = _futureNodeColor;

            // Boss levels override the color (always show boss red, regardless of state).
            bool isBoss = _runManager.IsBossLevel(i);
            if (isBoss && i != indicatorIndex)
                color = _bossNodeColor;
            // But the current boss level keeps the "current" highlight color so the
            // player can see where they are.

            if (nodeImage != null) nodeImage.color = color;

            // Set the level number text (optional child named "Number").
            Transform numberChild = nodeObj.transform.Find("Number");
            if (numberChild != null)
            {
                var text = numberChild.GetComponent<TMP_Text>();
                if (text != null) text.text = (i + 1).ToString();
            }

            // Show/hide the boss icon (optional child named "BossIcon").
            Transform bossIcon = nodeObj.transform.Find("BossIcon");
            if (bossIcon != null)
                bossIcon.gameObject.SetActive(isBoss);
        }

        // Position the current indicator above the active node.
        PositionIndicator(indicatorIndex);
    }

    /// <summary>
    /// Moves the current-indicator chevron to sit above the given node index.
    /// If the index is out of range (e.g., past the end after winning the last
    /// level), the indicator is hidden.
    /// </summary>
    void PositionIndicator(int nodeIndex)
    {
        if (_currentIndicator == null) return;

        // Past the end (e.g., won the last level) — hide the indicator.
        if (nodeIndex < 0 || nodeIndex >= _spawnedNodes.Count)
        {
            _currentIndicator.gameObject.SetActive(false);
            return;
        }

        _currentIndicator.gameObject.SetActive(true);

        // Force a layout rebuild so the nodes have their final positions.
        UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(_progressBarParent);

        GameObject targetNode = _spawnedNodes[nodeIndex];
        RectTransform nodeRT = targetNode.transform as RectTransform;
        RectTransform barRT = _progressBarParent;

        if (nodeRT == null || barRT == null) return;

        // The indicator is a child of _progressBarParent, so we set its
        // anchoredPosition to match the node's X position within the bar.
        // Use the node's local position relative to the bar.
        Vector3 nodeLocalPos = barRT.InverseTransformPoint(nodeRT.position);
        Vector2 indicatorPos = _currentIndicator.anchoredPosition;
        indicatorPos.x = ((Vector2)nodeLocalPos).x;
        _currentIndicator.anchoredPosition = indicatorPos;
    }

    // -----------------------------------------------------------------------
    // Rewards + context
    // -----------------------------------------------------------------------

    void PopulateRewards()
    {
        if (_rewardsPanel == null || _runManager == null) return;
        if (_runManager.LastBattleResult == null)
        {
            // No last battle result — hide the rewards panel.
            _rewardsPanel.Hide();
            return;
        }
        _rewardsPanel.Populate(_runManager.LastBattleResult);
    }

    /// <summary>
    /// Shows the context-appropriate navigation button based on LastBattleResult.
    /// Hides the others.
    /// </summary>
    void ShowContextButton()
    {
        // Default: hide all.
        if (_nextLevelButton != null) _nextLevelButton.gameObject.SetActive(false);
        if (_retryBossButton != null) _retryBossButton.gameObject.SetActive(false);
        if (_returnToMenuButton != null) _returnToMenuButton.gameObject.SetActive(false);
        if (_restartRunButton != null) _restartRunButton.gameObject.SetActive(false);

        if (_runManager == null || _runManager.LastBattleResult == null)
        {
            // No last battle result — this shouldn't happen in normal flow,
            // but show "Return to Menu" as a fallback.
            if (_returnToMenuButton != null) _returnToMenuButton.gameObject.SetActive(true);
            if (_statusText != null) _statusText.text = "No battle result.";
            return;
        }

        var result = _runManager.LastBattleResult;

        if (result.PlayerWon)
        {
            if (result.WasLastLevel)
            {
                // Run complete — return to menu.
                if (_returnToMenuButton != null) _returnToMenuButton.gameObject.SetActive(true);
                if (_statusText != null) _statusText.text = "Run Complete!";
            }
            else
            {
                // Won — next level.
                if (_nextLevelButton != null) _nextLevelButton.gameObject.SetActive(true);
                if (_statusText != null) _statusText.text = "Victory!";
            }
        }
        else
        {
            if (_runManager.Hearts <= 0)
            {
                // Run over — show both "Return to Menu" and "Restart Run".
                if (_returnToMenuButton != null) _returnToMenuButton.gameObject.SetActive(true);
                if (_restartRunButton != null) _restartRunButton.gameObject.SetActive(true);
                if (_statusText != null) _statusText.text = "Run Over";
            }
            else if (result.IsBoss)
            {
                // Boss loss — retry.
                if (_retryBossButton != null) _retryBossButton.gameObject.SetActive(true);
                if (_statusText != null) _statusText.text = "Defeated — retry the boss?";
            }
            else
            {
                // Non-boss loss — skip to next.
                if (_nextLevelButton != null) _nextLevelButton.gameObject.SetActive(true);
                if (_statusText != null) _statusText.text = "Defeated — advance to the next level.";
            }
        }
    }

    // -----------------------------------------------------------------------
    // Hearts + level text
    // -----------------------------------------------------------------------

    void UpdateHearts()
    {
        if (_runManager == null) return;
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
        // Show the level the player is ABOUT TO PLAY (indicator index + 1).
        int displayLevel = Mathf.Clamp(IndicatorLevelIndex + 1, 1, _runManager.TotalLevels);
        _levelText.text = $"Level {displayLevel} / {_runManager.TotalLevels}";
    }
}
