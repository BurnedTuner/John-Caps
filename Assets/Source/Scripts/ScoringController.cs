using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public enum MatchResult
{
    InProgress = 0,
    PlayerVictory = 1,
    OpponentVictory = 2
}

/// <summary>
/// Recalculates the score after each completed throw and presents it as a signed tug-of-war value.
/// Positive advantage belongs to the player; negative advantage belongs to the opponent.
/// </summary>
[DefaultExecutionOrder(100)]
[DisallowMultipleComponent]
public sealed class ScoringController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private ScoringZone _scoringZone;
    [SerializeField] private CapTurnResolver _turnResolver;
    [SerializeField] private GameManager _gameManager;
    [SerializeField] private CapThrower[] _turnSources;

    [Header("Rules")]
    [SerializeField, Min(1)] private int _maximumAdvantage = 5;
    [SerializeField] private bool _lockTurnsWhenMatchFinished = true;

    [Header("Tug-of-war UI (optional)")]
    [SerializeField] private Slider _advantageSlider;
    [SerializeField] private TMP_Text _playerCountText;
    [SerializeField] private TMP_Text _opponentCountText;
    [SerializeField] private TMP_Text _differenceText;
    [SerializeField] private TMP_Text _resultText;

    [Header("Result messages")]
    [SerializeField] private string _playerVictoryMessage = "ПОБЕДА";
    [SerializeField] private string _opponentVictoryMessage = "ПОРАЖЕНИЕ";

    public int PlayerCaps { get; private set; }
    public int OpponentCaps { get; private set; }
    public int Advantage => PlayerCaps - OpponentCaps;
    public int MaximumAdvantage => _maximumAdvantage;
    public MatchResult CurrentResult { get; private set; } = MatchResult.InProgress;

    public event Action<int, int, int> ScoreChanged;
    public event Action<MatchResult> MatchFinished;

    void Awake()
    {
        ResolveReferences();
        ConfigureSlider();
    }

    void OnEnable()
    {
        ResolveReferences();
        SubscribeToTurns();
    }

    void Start()
    {
        RecalculateScore();
    }

    void OnDisable()
    {
        UnsubscribeFromTurns();
    }

    void OnValidate()
    {
        _maximumAdvantage = Mathf.Max(1, _maximumAdvantage);
        ConfigureSlider();
    }

    void ResolveReferences()
    {
        if (_scoringZone == null)
            _scoringZone = FindFirstObjectByType<ScoringZone>();

        if (_turnResolver == null)
            _turnResolver = FindFirstObjectByType<CapTurnResolver>();

        if (_gameManager == null)
            _gameManager = FindFirstObjectByType<GameManager>();

        if (_turnSources == null || _turnSources.Length == 0)
            _turnSources = FindObjectsByType<CapThrower>(FindObjectsSortMode.None);
    }

    void SubscribeToTurns()
    {
        if (_turnResolver != null)
        {
            _turnResolver.OnTurnFinished -= HandleBoardChanged;
            _turnResolver.OnTurnFinished += HandleBoardChanged;
        }

        if (_gameManager != null)
        {
            _gameManager.OnBoardReset -= HandleBoardReset;
            _gameManager.OnBoardReset += HandleBoardReset;
        }
    }

    void UnsubscribeFromTurns()
    {
        if (_turnResolver != null)
            _turnResolver.OnTurnFinished -= HandleBoardChanged;

        if (_gameManager != null)
            _gameManager.OnBoardReset -= HandleBoardReset;
    }

    void HandleBoardChanged(CapTurnResolver _)
    {
        if (CurrentResult == MatchResult.InProgress)
            RecalculateScore();
    }

    void HandleBoardReset(GameManager _)
    {
        ResetMatch();
    }

    public void RecalculateScore()
    {
        if (_scoringZone == null)
        {
            Debug.LogWarning("[ScoringController] ScoringZone is not assigned.", this);
            return;
        }

        CapCounts counts = _scoringZone.GetCapCounts();
        PlayerCaps = counts.Player;
        OpponentCaps = counts.Opponent;

        UpdateUI();
        ScoreChanged?.Invoke(PlayerCaps, OpponentCaps, Advantage);

        if (CurrentResult != MatchResult.InProgress) return;

        if (Advantage >= _maximumAdvantage)
            FinishMatch(MatchResult.PlayerVictory);
        else if (Advantage <= -_maximumAdvantage)
            FinishMatch(MatchResult.OpponentVictory);
    }

    public void ResetMatch()
    {
        SetTurnSourcesEnabled(true);
        CurrentResult = MatchResult.InProgress;
        RecalculateScore();
    }

    void FinishMatch(MatchResult result)
    {
        CurrentResult = result;
        if (_lockTurnsWhenMatchFinished)
            SetTurnSourcesEnabled(false);
        UpdateUI();
        MatchFinished?.Invoke(result);
    }

    void SetTurnSourcesEnabled(bool enabled)
    {
        if (_turnSources == null) return;

        foreach (CapThrower turnSource in _turnSources)
        {
            if (turnSource != null)
                turnSource.SetTurnInputEnabled(enabled);
        }
    }

    void ConfigureSlider()
    {
        if (_advantageSlider == null) return;

        _advantageSlider.minValue = -_maximumAdvantage;
        _advantageSlider.maxValue = _maximumAdvantage;
        _advantageSlider.wholeNumbers = true;
    }

    void UpdateUI()
    {
        ConfigureSlider();

        if (_advantageSlider != null)
            _advantageSlider.SetValueWithoutNotify(Mathf.Clamp(Advantage, -_maximumAdvantage, _maximumAdvantage));

        if (_playerCountText != null)
            _playerCountText.text = PlayerCaps.ToString();

        if (_opponentCountText != null)
            _opponentCountText.text = OpponentCaps.ToString();

        if (_differenceText != null)
            _differenceText.text = Advantage.ToString("+0;-0;0");

        if (_resultText != null)
        {
            _resultText.text = CurrentResult switch
            {
                MatchResult.PlayerVictory => _playerVictoryMessage,
                MatchResult.OpponentVictory => _opponentVictoryMessage,
                _ => string.Empty
            };
        }
    }
}
