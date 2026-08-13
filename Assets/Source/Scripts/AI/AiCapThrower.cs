using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Throw controller for the opponent — the mirror of CapThrower with the input replaced by a search.
/// It takes the next cap from the pool, asks AiMoveSearch where to put it and submits the result to
/// the same CapTurnResolver the player throws into, so both sides go through identical rules.
/// </summary>
[DisallowMultipleComponent]
public sealed class AiCapThrower : MonoBehaviour
{
    public enum State { Idle, Thinking, WaitingForResolution }

    [Header("References")]
    [SerializeField] private CapTurnResolver _turnResolver;
    [SerializeField] private CapFieldBoundary _fieldBoundary;
    [SerializeField] private OpponentCapPool _pool;
    [Tooltip("The player's thrower. Used to read its throw power for the danger metric.")]
    [SerializeField] private CapThrower _playerThrower;
    [SerializeField] private GameManager _gameManager;

    [Header("Behaviour")]
    [Tooltip("Pause before the opponent throws, so the turn is readable instead of instantaneous.")]
    [Min(0f)][SerializeField] private float _thinkDelay = 0.6f;

    [SerializeField] private AiSearchSettings _search = new();

    [Header("Debug")]
    [Tooltip("After every AI turn, recount the board and compare it with what the search predicted. " +
             "CapBoardSimulation is a hand-written copy of the throw rules, and this is the only thing " +
             "that notices when it drifts away from CapTurnResolver.")]
    [SerializeField] private bool _verifyPrediction;

    public State CurrentState { get; private set; } = State.Idle;

    /// <summary>The opponent's cap waiting at the spawn point. Board readers must skip it.</summary>
    public Cap WaitingCap => _waitingCap;

    /// <summary>False once the deck is exhausted and no cap is waiting — the turn has to be passed.</summary>
    public bool HasCapToThrow => _pool != null && (_waitingCap != null || !_pool.IsEmpty);

    /// <summary>Raised when the opponent cannot take its turn, so the turn controller can move on.</summary>
    public event System.Action<AiCapThrower> TurnSkipped;

    private readonly AiMoveSearch _moveSearch = new();
    private readonly List<Cap> _candidateCaps = new();

    private CapTuning _tuning;
    private Cap _waitingCap;
    private float _thinkTimer;
    private AiMove _lastMove;
    private bool _hasLastMove;
    private string _lastSkipReason;

    private CapBoardSimulation _predictionCheck;

    void Awake()
    {
        _tuning = CapTuning.Instance;
        ResolveReferences();
    }

    void OnEnable()
    {
        ResolveReferences();
        Subscribe();
    }

    void Start()
    {
        ResolveReferences();

        if (_turnResolver == null)
            Debug.LogError("[AiCapThrower] CapTurnResolver is not assigned or present in the scene.", this);

        if (_pool == null)
            Debug.LogError("[AiCapThrower] OpponentCapPool is not assigned.", this);
        else if (_fieldBoundary != null && _fieldBoundary.Supports(CapMath.ToXZ(_pool.SpawnPosition), 0f))
        {
            Debug.LogWarning(
                "[AiCapThrower] The opponent spawn point sits on the field. Move it outside the field, " +
                "otherwise the waiting cap is treated as a board cap and eventually falls off.", this);
        }
    }

    void OnDisable()
    {
        Unsubscribe();
    }

    /// <summary>
    /// Hands the search the rules the match is actually being played by. TurnController owns them, and
    /// a search that optimises a different rulebook picks the wrong move — see AiSearchSettings.
    /// </summary>
    public void ApplyTurnRules(bool neutralGrantsExtraTurn, bool stackedCapsCountAsOnField)
    {
        if (_search == null) return;

        _search.NeutralGrantsExtraTurn = neutralGrantsExtraTurn;
        _search.StackedCapsCountAsOnField = stackedCapsCountAsOnField;
    }

    void ResolveReferences()
    {
        if (_tuning == null) _tuning = CapTuning.Instance;
        if (_turnResolver == null) _turnResolver = FindFirstObjectByType<CapTurnResolver>();
        if (_fieldBoundary == null) _fieldBoundary = FindFirstObjectByType<CapFieldBoundary>();
        if (_pool == null) _pool = GetComponent<OpponentCapPool>();
        if (_pool == null) _pool = FindFirstObjectByType<OpponentCapPool>();
        if (_playerThrower == null) _playerThrower = FindFirstObjectByType<CapThrower>();
        if (_gameManager == null) _gameManager = FindFirstObjectByType<GameManager>();
    }

    void Subscribe()
    {
        if (_turnResolver != null)
        {
            _turnResolver.OnTurnFinished -= HandleTurnFinished;
            _turnResolver.OnTurnFinished += HandleTurnFinished;
        }

        if (_gameManager != null)
        {
            _gameManager.OnBoardReset -= HandleBoardReset;
            _gameManager.OnBoardReset += HandleBoardReset;
        }
    }

    void Unsubscribe()
    {
        if (_turnResolver != null)
            _turnResolver.OnTurnFinished -= HandleTurnFinished;

        if (_gameManager != null)
            _gameManager.OnBoardReset -= HandleBoardReset;
    }

    /// <summary>Starts the opponent's turn. Raises TurnSkipped instead when there is nothing to throw.</summary>
    public void BeginTurn()
    {
        // Passing the turn back is the only safe answer here. Returning quietly would leave the turn
        // controller waiting for a throw that is never coming, with the resolver idle and its watchdog
        // therefore never counting — the match would simply stop.
        if (CurrentState != State.Idle)
        {
            SkipTurn($"a turn started while the previous one was still in {CurrentState}");
            return;
        }

        EnsureWaitingCap();

        if (_waitingCap == null)
        {
            SkipTurn("the deck is empty");
            return;
        }

        _thinkTimer = _thinkDelay;
        CurrentState = State.Thinking;
    }

    /// <summary>
    /// Drops whatever the opponent was in the middle of, so a new turn can be started. Used by
    /// TurnController's watchdog; the cap already taken out of the deck is not given back, because
    /// the throw it was taken for may well have happened.
    /// </summary>
    public void AbortTurn()
    {
        _hasLastMove = false;
        _lastSkipReason = null;
        CurrentState = State.Idle;
    }

    void Update()
    {
        if (CurrentState != State.Thinking) return;

        // Unscaled, because ImpactFeedback drives Time.timeScale for its hit-stop and the opponent
        // should not appear to hesitate longer just because the previous throw landed hard.
        _thinkTimer -= Time.unscaledDeltaTime;
        if (_thinkTimer > 0f) return;

        Throw();
    }

    void Throw()
    {
        if (_turnResolver == null || _pool == null || _waitingCap == null)
        {
            SkipTurn("the opponent has no cap or no resolver to throw into");
            return;
        }

        // With a deck only the top cap is available, so the list holds one entry. A hand-style pool
        // would pass several and the same search would choose the cap as well as the landing point.
        _candidateCaps.Clear();
        _candidateCaps.Add(_waitingCap);

        bool found = _moveSearch.TryFindBestMove(
            _tuning,
            _fieldBoundary,
            _search,
            _candidateCaps,
            ResolvePlayerThrowPower(),
            out AiMove move);

        if (!found || !move.IsValid)
        {
            SkipTurn("the search found no legal landing point");
            return;
        }

        _lastMove = move;
        _hasLastMove = true;
        move.Cap.SetParked(false);

        var request = new CapThrowRequest(
            move.Cap,
            _pool.SpawnPosition,
            CapMath.FromXZ(move.LandingPoint, 0f),
            move.Cap.Parameters.ThrowPower);

        if (!_turnResolver.TryStartThrow(request))
        {
            // Put the cap back exactly as it was: leaving parking already snapped its transform down
            // to the ground plane, and parking again does not undo that on its own.
            move.Cap.SetParked(true);
            move.Cap.transform.position = _pool.SpawnPosition;
            _hasLastMove = false;
            SkipTurn("CapTurnResolver refused the throw");
            return;
        }

        _pool.Consume();
        _waitingCap = null;
        CurrentState = State.WaitingForResolution;
    }

    void SkipTurn(string reason)
    {
        // Only the first of a run of identical skips is worth a line in the console: a side that
        // cannot act usually cannot act next turn either.
        if (reason != _lastSkipReason)
        {
            _lastSkipReason = reason;
            Debug.LogWarning($"[AiCapThrower] Skipping the turn: {reason}.", this);
        }

        CurrentState = State.Idle;
        TurnSkipped?.Invoke(this);
    }

    void EnsureWaitingCap()
    {
        if (_waitingCap != null || _pool == null) return;
        _waitingCap = _pool.SpawnNext();
    }

    /// <summary>
    /// Force the danger metric assumes the player can bring next turn. It only affects how cautious
    /// the AI is about its own caps, never how hard it throws.
    /// </summary>
    float ResolvePlayerThrowPower()
    {
        if (_search.PlayerThrowPowerOverride > 0f)
            return _search.PlayerThrowPowerOverride;

        if (_playerThrower != null && _playerThrower.CapPrefab != null)
            return _playerThrower.CapPrefab.Parameters.ThrowPower;

        return _waitingCap != null ? _waitingCap.Parameters.ThrowPower : 5f;
    }

    void HandleTurnFinished(CapTurnResolver resolver)
    {
        if (resolver != _turnResolver || CurrentState != State.WaitingForResolution) return;

        CurrentState = State.Idle;
        _lastSkipReason = null;

        if (_verifyPrediction && _hasLastMove)
            VerifyPrediction();

        _hasLastMove = false;
    }

    /// <summary>
    /// Compares the board the search promised against the one the engine actually produced.
    /// A mismatch means CapBoardSimulation and CapTurnResolver no longer agree on the rules, which is
    /// otherwise completely silent — the AI just starts playing badly for no visible reason.
    /// </summary>
    void VerifyPrediction()
    {
        if (_tuning == null) return;

        _predictionCheck ??= new CapBoardSimulation();

        var actual = new CapSimResult();
        _predictionCheck.CaptureAndTally(
            _tuning,
            _fieldBoundary,
            CapRegistry.AllCaps,
            _search.StackedCapsCountAsOnField,
            ResolvePlayerThrowPower(),
            ref actual);

        CapSimResult predicted = _lastMove.Result;

        if (predicted.PlayerRemaining == actual.PlayerRemaining
            && predicted.OpponentRemaining == actual.OpponentRemaining
            && predicted.NeutralRemaining == actual.NeutralRemaining
            && predicted.PlayerStacked == actual.PlayerStacked
            && predicted.OpponentStacked == actual.OpponentStacked)
            return;

        Debug.LogWarning(
            $"[AiCapThrower] The simulation and the engine disagree about the throw at " +
            $"{_lastMove.LandingPoint}.\n" +
            $"  predicted: player {predicted.PlayerRemaining} (stacked {predicted.PlayerStacked}), " +
            $"opponent {predicted.OpponentRemaining} (stacked {predicted.OpponentStacked}), " +
            $"neutral {predicted.NeutralRemaining}\n" +
            $"  actual:    player {actual.PlayerRemaining} (stacked {actual.PlayerStacked}), " +
            $"opponent {actual.OpponentRemaining} (stacked {actual.OpponentStacked}), " +
            $"neutral {actual.NeutralRemaining}", this);
    }

    void HandleBoardReset(GameManager gameManager)
    {
        if (gameManager != _gameManager) return;

        // ResetBoard destroys every registered cap, the waiting one included.
        _waitingCap = null;
        _hasLastMove = false;
        _lastSkipReason = null;
        CurrentState = State.Idle;
        _pool?.Rebuild();
    }

    void OnDrawGizmos()
    {
        if (_search == null || !_search.DrawGizmos) return;

        IReadOnlyList<AiMoveSearch.ScoredPoint> points = _moveSearch.LastEvaluatedPoints;
        if (points == null || points.Count == 0) return;

        float range = Mathf.Max(0.0001f, _moveSearch.LastBestScore - _moveSearch.LastWorstScore);
        var cold = new Color(0.1f, 0.2f, 0.7f, 0.3f);
        var hot = new Color(1f, 0.3f, 0.05f, 0.9f);

        for (int i = 0; i < points.Count; i++)
        {
            float t = Mathf.Clamp01((points[i].Score - _moveSearch.LastWorstScore) / range);
            Gizmos.color = Color.Lerp(cold, hot, t);
            Gizmos.DrawSphere(CapMath.FromXZ(points[i].Point, 0.05f), 0.08f + 0.2f * t);
        }

        if (_hasLastMove)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(CapMath.FromXZ(_lastMove.LandingPoint, 0.05f), 0.6f);
        }
    }
}
