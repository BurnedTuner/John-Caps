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
/// The OnBattleEnded event now carries a <see cref="BattleResult"/> with
/// immutable cap snapshots for the rewards UI. This component forwards the
/// result to an optional <see cref="MatchRewardsPanel"/> child.
///
/// Setup (per battle scene):
/// 1. Add this to a GameObject in the battle scene.
/// 2. Assign panels: _winPanel, _loseSkipPanel, _loseBossPanel, _runOverPanel, _runVictoryPanel.
///    Set all panels inactive by default.
/// 3. Assign buttons on each panel: _nextLevelButton, _loseContinueButton, _bossRetryButton, _returnToMenuButton, _victoryReturnButton.
/// 4. Assign heart images (3 Images — disabled left-to-right as hearts are lost).
/// 5. (Optional) Assign _levelText and _mainMenuSceneName.
/// 6. (Optional) Assign _rewardsPanel to enable the match-rewards UI (shows
///    caps gained/lost with stickers and hover tooltips).
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

    [Header("Rewards UI")]
    [Tooltip("Optional: the match-rewards panel that shows caps gained/lost. " +
             "If assigned, this panel is shown alongside whichever post-battle " +
             "panel activates. The rewards panel self-subscribes to RunManager " +
             "events, so this reference is only used to hide it when the " +
             "post-battle panels are dismissed.")]
    [SerializeField] private MatchRewardsPanel _rewardsPanel;

    [Header("Scene config")]
    [Tooltip("The main menu scene name (loaded when the run ends).")]
    [SerializeField] private string _mainMenuSceneName = "MainMenu";

    private RunManager _runManager;

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
    }

    void UnwireButtons()
    {
        if (_nextLevelButton != null) _nextLevelButton.onClick.RemoveListener(OnNextLevel);
        if (_loseContinueButton != null) _loseContinueButton.onClick.RemoveListener(OnLoseContinue);
        if (_bossRetryButton != null) _bossRetryButton.onClick.RemoveListener(OnBossRetry);
        if (_returnToMenuButton != null) _returnToMenuButton.onClick.RemoveListener(OnReturnToMenu);
        if (_victoryReturnButton != null) _victoryReturnButton.onClick.RemoveListener(OnReturnToMenu);
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

    void OnNextLevel()
    {
        HideAllPanels();
        HideRewardsPanel();
        _runManager?.AdvanceToNextLevel();
    }

    void OnLoseContinue()
    {
        // Non-boss loss: skip to next level.
        HideAllPanels();
        HideRewardsPanel();
        _runManager?.AdvanceToNextLevel();
    }

    void OnBossRetry()
    {
        // Boss loss: retry the same level.
        HideAllPanels();
        HideRewardsPanel();
        _runManager?.RestartCurrentLevel();
    }

    void OnReturnToMenu()
    {
        HideAllPanels();
        HideRewardsPanel();
        if (!string.IsNullOrEmpty(_mainMenuSceneName))
            SceneManager.LoadScene(_mainMenuSceneName);
    }

    void HideRewardsPanel()
    {
        // The rewards panel self-manages its visibility via OnBattleEnded,
        // but we also hide it here so it disappears when the player clicks
        // a navigation button (Next / Continue / Retry / Return).
        if (_rewardsPanel != null)
            _rewardsPanel.Hide();

        // Clear LastBattleResult so the NEXT scene's MatchRewardsPanel doesn't
        // see a stale result. RunManager persists via DontDestroyOnLoad, so without
        // this, the new scene's panel would see the previous battle's result and
        // (if it auto-populates) re-show itself.
        if (_runManager != null)
            _runManager.ClearLastBattleResult();
    }

    // -----------------------------------------------------------------------
    // Event handlers
    // -----------------------------------------------------------------------

    void HandleBattleEnded(BattleResult result)
    {
        if (_runManager == null || result == null) return;

        bool playerWon = result.PlayerWon;

        if (playerWon)
        {
            // Check if this was the last level (victory).
            if (result.WasLastLevel)
            {
                ShowPanel(_runVictoryPanel);
            }
            else
            {
                ShowPanel(_winPanel);
            }
        }
        else
        {
            if (_runManager.Hearts <= 0)
            {
                // No hearts left — run over.
                ShowPanel(_runOverPanel);
            }
            else if (result.IsBoss)
            {
                // Boss loss with hearts — retry.
                ShowPanel(_loseBossPanel);
            }
            else
            {
                // Non-boss loss — skip to next level.
                ShowPanel(_loseSkipPanel);
            }
        }

        UpdateLevelText();

        // The rewards panel self-populates via its own OnBattleEnded subscription.
        // If a rewards panel is assigned here, ensure it's visible (in case it
        // was hidden by a previous panel-dismiss action).
        if (_rewardsPanel != null && _rewardsPanel.IsVisible == false)
        {
            // Force it to show + populate from the last result if it missed the event.
            if (_runManager.LastBattleResult != null)
                _rewardsPanel.Populate(_runManager.LastBattleResult);
        }
    }

    void HandleHeartsChanged(int newHearts)
    {
        UpdateHearts(newHearts);
    }

    void HandleRunEnded(bool isVictory)
    {
        if (isVictory)
            ShowPanel(_runVictoryPanel);
        else
            ShowPanel(_runOverPanel);
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
