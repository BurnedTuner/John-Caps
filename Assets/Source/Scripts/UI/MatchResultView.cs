using TMPro;
using UnityEngine;

/// <summary>
/// Shows the match outcome as a large caption once TurnController reports a winner, and clears it
/// again when the board is reset.
///
/// The caption is driven through a CanvasGroup so it can fade and pop in without touching the child
/// texts, and it never blocks raycasts — the menu button underneath stays clickable.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(CanvasGroup))]
public sealed class MatchResultView : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private TurnController _turnController;
    [SerializeField] private GameManager _gameManager;

    [Tooltip("The big caption itself.")]
    [SerializeField] private TMP_Text _resultText;

    [Tooltip("Optional smaller line under the caption, e.g. how to restart.")]
    [SerializeField] private TMP_Text _hintText;

    [Header("Messages")]
    [SerializeField] private string _playerVictoryMessage = "ПОБЕДА";
    [SerializeField] private string _opponentVictoryMessage = "ПОРАЖЕНИЕ";
    [Tooltip("Shown when neither side could finish the match, e.g. nobody had a cap left to throw.")]
    [SerializeField] private string _drawMessage = "НИЧЬЯ";
    [SerializeField] private string _hintMessage = "R — начать заново";

    [Header("Colours")]
    [SerializeField] private Color _victoryColor = new Color(0.25f, 1f, 0.6f);
    [SerializeField] private Color _defeatColor = new Color(1f, 0.3f, 0.25f);
    [SerializeField] private Color _drawColor = new Color(0.9f, 0.9f, 0.9f);

    [Header("Appearance")]
    [Tooltip("How long the caption takes to fade and settle into place.")]
    [Min(0f)][SerializeField] private float _appearDuration = 0.35f;

    [Tooltip("Scale the caption starts at before it settles to 1. Above 1 it drops in, below 1 it grows.")]
    [Min(0.1f)][SerializeField] private float _startScale = 1.4f;

    /// <summary>True while the caption is on screen.</summary>
    public bool IsShown { get; private set; }

    private CanvasGroup _canvasGroup;
    private RectTransform _rectTransform;
    private float _appearElapsed;
    private bool _isAnimating;

    void Awake()
    {
        _canvasGroup = GetComponent<CanvasGroup>();
        _rectTransform = GetComponent<RectTransform>();

        // The caption is purely decorative, so it must never swallow clicks meant for the UI below it.
        _canvasGroup.interactable = false;
        _canvasGroup.blocksRaycasts = false;

        ResolveReferences();
        Hide();
    }

    void OnEnable()
    {
        ResolveReferences();
        Subscribe();
    }

    void Start()
    {
        if (_turnController == null)
            Debug.LogError("[MatchResultView] TurnController is not assigned or present in the scene.", this);

        if (_resultText == null)
            Debug.LogError("[MatchResultView] Result text is not assigned, so there is nothing to show.", this);
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
            _turnController.MatchFinished -= HandleMatchFinished;

        if (_gameManager != null)
            _gameManager.OnBoardReset -= HandleBoardReset;
    }

    void Update()
    {
        if (!_isAnimating) return;

        // Unscaled time, because ImpactFeedback drives Time.timeScale for its hit-stop and the
        // caption should not stutter along with it.
        _appearElapsed += Time.unscaledDeltaTime;

        float t = _appearDuration > 0f ? Mathf.Clamp01(_appearElapsed / _appearDuration) : 1f;
        float eased = 1f - (1f - t) * (1f - t) * (1f - t);

        _canvasGroup.alpha = t;
        if (_rectTransform != null)
            _rectTransform.localScale = Vector3.one * Mathf.LerpUnclamped(_startScale, 1f, eased);

        if (t >= 1f) _isAnimating = false;
    }

    void HandleMatchFinished(CapOwner winner)
    {
        if (_resultText != null)
        {
            _resultText.text = winner switch
            {
                CapOwner.Player => _playerVictoryMessage,
                CapOwner.Opponent => _opponentVictoryMessage,
                _ => _drawMessage
            };

            _resultText.color = winner switch
            {
                CapOwner.Player => _victoryColor,
                CapOwner.Opponent => _defeatColor,
                _ => _drawColor
            };
        }

        if (_hintText != null)
            _hintText.text = _hintMessage;

        Show();
    }

    void HandleBoardReset(GameManager gameManager)
    {
        if (gameManager != _gameManager) return;
        Hide();
    }

    /// <summary>Fades the caption in. Public so a custom end-of-match flow can trigger it directly.</summary>
    public void Show()
    {
        IsShown = true;
        _isAnimating = true;
        _appearElapsed = 0f;
        _canvasGroup.alpha = 0f;

        if (_rectTransform != null)
            _rectTransform.localScale = Vector3.one * _startScale;
    }

    public void Hide()
    {
        IsShown = false;
        _isAnimating = false;
        _appearElapsed = 0f;
        _canvasGroup.alpha = 0f;

        if (_rectTransform != null)
            _rectTransform.localScale = Vector3.one;
    }
}
