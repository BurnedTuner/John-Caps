using TMPro;
using UnityEngine;

/// <summary>
/// Tells the player whose turn it is and, more importantly, why the turn sometimes does not change
/// hands: knocking an enemy cap off the field earns another throw.
///
/// Two independent pieces, both optional — assign only the texts you actually want:
/// a permanent line with the current side and its streak, and a short banner that pops up whenever
/// either side earns an extra turn. The banner fires for the opponent too, otherwise the AI throwing
/// three times in a row looks like a bug rather than the rule working.
/// </summary>
[DisallowMultipleComponent]
public sealed class TurnBannerView : MonoBehaviour
{
    enum BannerPhase { Hidden, FadingIn, Holding, FadingOut }

    [Header("References")]
    [SerializeField] private TurnController _turnController;
    [SerializeField] private GameManager _gameManager;

    [Tooltip("Always-on line telling whose turn it is. Optional.")]
    [SerializeField] private TMP_Text _turnText;

    [Tooltip("Root of the pop-up shown when someone earns an extra turn. Optional.")]
    [SerializeField] private CanvasGroup _extraTurnGroup;

    [Tooltip("Caption inside the extra-turn pop-up.")]
    [SerializeField] private TMP_Text _extraTurnText;

    [Header("Turn line")]
    [SerializeField] private string _playerTurnMessage = "ТВОЙ ХОД";
    [SerializeField] private string _opponentTurnMessage = "ХОД ПРОТИВНИКА";

    [Tooltip("Appended while a side is on a streak. {0} is the streak length. Empty disables it.")]
    [SerializeField] private string _streakSuffix = "   СЕРИЯ ×{0}";

    [SerializeField] private Color _playerColor = new Color(0.3f, 0.95f, 0.9f);
    [SerializeField] private Color _opponentColor = new Color(1f, 0.4f, 0.25f);

    [Header("Extra turn pop-up")]
    [Tooltip("{0} is the caps knocked off with the right word form, {1} is the streak length.")]
    [SerializeField] private string _playerExtraTurnMessage = "СБИТО {0} — БРОСАЙ СНОВА!";

    [SerializeField] private string _opponentExtraTurnMessage = "ПРОТИВНИК СБИЛ {0} — ХОДИТ СНОВА";

    [Tooltip("Word forms for the cap count: for 1, for 2-4, for 5 and more.")]
    [SerializeField] private string[] _capWordForms = { "ФИШКА", "ФИШКИ", "ФИШЕК" };

    [Header("Pop-up timing")]
    [Min(0f)][SerializeField] private float _fadeInDuration = 0.15f;
    [Min(0f)][SerializeField] private float _holdDuration = 1.4f;
    [Min(0f)][SerializeField] private float _fadeOutDuration = 0.45f;

    [Tooltip("Scale the pop-up starts at before it settles to 1.")]
    [Min(0.1f)][SerializeField] private float _startScale = 1.25f;

    private RectTransform _extraTurnRect;
    private BannerPhase _phase = BannerPhase.Hidden;
    private float _phaseElapsed;

    void Awake()
    {
        if (_extraTurnGroup != null)
        {
            _extraTurnRect = _extraTurnGroup.GetComponent<RectTransform>();
            _extraTurnGroup.interactable = false;
            _extraTurnGroup.blocksRaycasts = false;
        }

        ResolveReferences();
        HideBanner();
    }

    void OnEnable()
    {
        ResolveReferences();
        Subscribe();
    }

    void Start()
    {
        if (_turnController == null)
        {
            Debug.LogError("[TurnBannerView] TurnController is not assigned or present in the scene.", this);
            return;
        }

        // The first turn starts before this component can subscribe, so catch up with it here.
        if (_turnController.CurrentTurn != CapOwner.Neutral)
            UpdateTurnText(_turnController.CurrentTurn, _turnController.ConsecutiveTurns);
    }

    void OnDisable()
    {
        Unsubscribe();
    }

    void ResolveReferences()
    {
        if (_turnController == null) _turnController = FindFirstObjectByType<TurnController>();
        if (_gameManager == null) _gameManager = FindFirstObjectByType<GameManager>();
    }

    void Subscribe()
    {
        if (_turnController != null)
        {
            _turnController.TurnStarted -= HandleTurnStarted;
            _turnController.TurnStarted += HandleTurnStarted;
            _turnController.ExtraTurnEarned -= HandleExtraTurnEarned;
            _turnController.ExtraTurnEarned += HandleExtraTurnEarned;
            _turnController.MatchFinished -= HandleMatchFinished;
            _turnController.MatchFinished += HandleMatchFinished;
        }

        if (_gameManager != null)
        {
            _gameManager.OnBoardReset -= HandleBoardReset;
            _gameManager.OnBoardReset += HandleBoardReset;
        }
    }

    void Unsubscribe()
    {
        if (_turnController != null)
        {
            _turnController.TurnStarted -= HandleTurnStarted;
            _turnController.ExtraTurnEarned -= HandleExtraTurnEarned;
            _turnController.MatchFinished -= HandleMatchFinished;
        }

        if (_gameManager != null)
            _gameManager.OnBoardReset -= HandleBoardReset;
    }

    void Update()
    {
        if (_phase == BannerPhase.Hidden || _extraTurnGroup == null) return;

        // Unscaled, because ImpactFeedback drives Time.timeScale for its hit-stop.
        _phaseElapsed += Time.unscaledDeltaTime;

        switch (_phase)
        {
            case BannerPhase.FadingIn:
            {
                float t = _fadeInDuration > 0f ? Mathf.Clamp01(_phaseElapsed / _fadeInDuration) : 1f;
                float eased = 1f - (1f - t) * (1f - t) * (1f - t);
                _extraTurnGroup.alpha = t;
                SetBannerScale(Mathf.LerpUnclamped(_startScale, 1f, eased));
                if (t >= 1f) EnterPhase(BannerPhase.Holding);
                break;
            }

            case BannerPhase.Holding:
            {
                _extraTurnGroup.alpha = 1f;
                if (_phaseElapsed >= _holdDuration) EnterPhase(BannerPhase.FadingOut);
                break;
            }

            case BannerPhase.FadingOut:
            {
                float t = _fadeOutDuration > 0f ? Mathf.Clamp01(_phaseElapsed / _fadeOutDuration) : 1f;
                _extraTurnGroup.alpha = 1f - t;
                if (t >= 1f) HideBanner();
                break;
            }
        }
    }

    void HandleTurnStarted(CapOwner owner)
    {
        UpdateTurnText(owner, _turnController != null ? _turnController.ConsecutiveTurns : 1);
    }

    void HandleExtraTurnEarned(ExtraTurnInfo info)
    {
        if (_extraTurnGroup == null) return;

        if (_extraTurnText != null)
        {
            string template = info.Owner == CapOwner.Player
                ? _playerExtraTurnMessage
                : _opponentExtraTurnMessage;

            string caps = $"{info.CapsKnockedOff} {PluralForm(info.CapsKnockedOff, _capWordForms)}";
            _extraTurnText.text = SafeFormat(template, caps, info.ConsecutiveTurns);
            _extraTurnText.color = info.Owner == CapOwner.Player ? _playerColor : _opponentColor;
        }

        ShowBanner();
    }

    void HandleMatchFinished(CapOwner winner, MatchEndReason reason)
    {
        // The match result caption takes over from here.
        HideBanner();
        if (_turnText != null) _turnText.text = string.Empty;
    }

    void HandleBoardReset(GameManager gameManager)
    {
        if (gameManager != _gameManager) return;
        HideBanner();
    }

    void UpdateTurnText(CapOwner owner, int consecutiveTurns)
    {
        if (_turnText == null) return;

        string message = owner == CapOwner.Player ? _playerTurnMessage : _opponentTurnMessage;

        if (consecutiveTurns > 1 && !string.IsNullOrEmpty(_streakSuffix))
            message += SafeFormat(_streakSuffix, consecutiveTurns);

        _turnText.text = message;
        _turnText.color = owner == CapOwner.Player ? _playerColor : _opponentColor;
    }

    void ShowBanner()
    {
        EnterPhase(BannerPhase.FadingIn);
        _extraTurnGroup.alpha = 0f;
        SetBannerScale(_startScale);
    }

    void HideBanner()
    {
        _phase = BannerPhase.Hidden;
        _phaseElapsed = 0f;

        if (_extraTurnGroup == null) return;
        _extraTurnGroup.alpha = 0f;
        SetBannerScale(1f);
    }

    void EnterPhase(BannerPhase phase)
    {
        _phase = phase;
        _phaseElapsed = 0f;
    }

    void SetBannerScale(float scale)
    {
        if (_extraTurnRect != null)
            _extraTurnRect.localScale = Vector3.one * scale;
    }

    /// <summary>
    /// Formats a designer-supplied template. These strings are typed in the inspector, so a stray
    /// placeholder must not throw: the exception would travel back up through TurnController into the
    /// resolver's event and take the turn loop down with it.
    /// </summary>
    static string SafeFormat(string template, params object[] arguments)
    {
        try
        {
            return string.Format(template, arguments);
        }
        catch (System.FormatException)
        {
            Debug.LogWarning($"[TurnBannerView] Bad placeholder in \"{template}\", showing it as is.");
            return template;
        }
    }

    /// <summary>
    /// Picks the Russian word form for a count: 1 фишка, 2-4 фишки, 5+ фишек.
    /// Returns the last form when fewer than three are configured.
    /// </summary>
    static string PluralForm(int count, string[] forms)
    {
        if (forms == null || forms.Length == 0) return string.Empty;
        if (forms.Length < 3) return forms[forms.Length - 1];

        int abs = Mathf.Abs(count);
        int lastTwo = abs % 100;
        if (lastTwo >= 11 && lastTwo <= 14) return forms[2];

        int last = abs % 10;
        if (last == 1) return forms[0];
        if (last >= 2 && last <= 4) return forms[1];
        return forms[2];
    }
}
