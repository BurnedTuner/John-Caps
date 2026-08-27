using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Tells the player whose turn it is and, more importantly, why the turn sometimes does not change
/// hands: knocking an enemy cap off the field earns another throw.
///
/// Three independent pieces, all optional — assign only the ones you want:
///
/// 1. TURN IMAGE (always visible): an Image whose sprite swaps based on whose
///    turn it is — _playerTurnSprite for player, _opponentTurnSprite for enemy.
///
/// 2. STREAK TEXT (always visible, optional): shows "×N" where N is the
///    consecutive turn count. Only shown when N > 1. Hidden otherwise.
///
/// 3. EXTRA TURN POP-UP (temporary): a CanvasGroup that fades in/out. Contains
///    an Image whose sprite swaps based on who earned the extra turn —
///    _playerExtraTurnSprite / _opponentExtraTurnSprite. No text on the pop-up
///    (replaced with an image per user request).
/// </summary>
[DisallowMultipleComponent]
public sealed class TurnBannerView : MonoBehaviour
{
    enum BannerPhase { Hidden, FadingIn, Holding, FadingOut }

    [Header("References")]
    [SerializeField] private TurnController _turnController;
    [SerializeField] private GameManager _gameManager;

    [Header("Turn image (always visible)")]
    [Tooltip("Image that shows whose turn it is. Sprite swaps between _playerTurnSprite and _opponentTurnSprite.")]
    [SerializeField] private Image _turnImage;

    [Tooltip("Sprite shown when it's the player's turn.")]
    [SerializeField] private Sprite _playerTurnSprite;

    [Tooltip("Sprite shown when it's the opponent's turn.")]
    [SerializeField] private Sprite _opponentTurnSprite;

    [Header("Streak text (optional, always visible)")]
    [Tooltip("Text showing the streak count as '×N'. Only visible when N > 1. Optional.")]
    [SerializeField] private TMP_Text _streakText;

    [Tooltip("Format for the streak text. {0} is the streak count.")]
    [SerializeField] private string _streakFormat = "×{0}";

    [Header("Extra turn pop-up")]
    [Tooltip("Root of the pop-up shown when someone earns an extra turn. Optional.")]
    [SerializeField] private CanvasGroup _extraTurnGroup;

    [Tooltip("Image inside the extra-turn pop-up. Sprite swaps based on who earned the extra turn.")]
    [SerializeField] private Image _extraTurnImage;

    [Tooltip("Sprite shown in the pop-up when the PLAYER earns an extra turn.")]
    [SerializeField] private Sprite _playerExtraTurnSprite;

    [Tooltip("Sprite shown in the pop-up when the OPPONENT earns an extra turn.")]
    [SerializeField] private Sprite _opponentExtraTurnSprite;

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
            UpdateTurnDisplay(_turnController.CurrentTurn, _turnController.ConsecutiveTurns);
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
        UpdateTurnDisplay(owner, _turnController != null ? _turnController.ConsecutiveTurns : 1);
    }

    void HandleExtraTurnEarned(ExtraTurnInfo info)
    {
        if (_extraTurnGroup == null) return;

        // Swap the pop-up image sprite based on who earned the extra turn.
        if (_extraTurnImage != null)
        {
            Sprite sprite = info.Owner == CapOwner.Player
                ? _playerExtraTurnSprite
                : _opponentExtraTurnSprite;
            if (sprite != null)
                _extraTurnImage.sprite = sprite;
        }

        ShowBanner();
    }

    void HandleMatchFinished(CapOwner winner, MatchEndReason reason)
    {
        HideBanner();
        // Hide the turn image + streak text on match end.
        if (_turnImage != null) _turnImage.gameObject.SetActive(false);
        if (_streakText != null) _streakText.gameObject.SetActive(false);
    }

    void HandleBoardReset(GameManager gameManager)
    {
        if (gameManager != _gameManager) return;
        HideBanner();
    }

    void UpdateTurnDisplay(CapOwner owner, int consecutiveTurns)
    {
        // Swap the turn image sprite.
        if (_turnImage != null)
        {
            _turnImage.gameObject.SetActive(true);
            Sprite sprite = owner == CapOwner.Player ? _playerTurnSprite : _opponentTurnSprite;
            if (sprite != null)
                _turnImage.sprite = sprite;
        }

        // Show streak text only when N > 1.
        if (_streakText != null)
        {
            if (consecutiveTurns > 1)
            {
                _streakText.gameObject.SetActive(true);
                _streakText.text = string.Format(_streakFormat, consecutiveTurns);
            }
            else
            {
                _streakText.gameObject.SetActive(false);
            }
        }
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
}
