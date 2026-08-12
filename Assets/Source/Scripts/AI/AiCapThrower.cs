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
    [Tooltip("The player's thrower. Used to skip its waiting cap and to read its throw power.")]
    [SerializeField] private CapThrower _playerThrower;
    [SerializeField] private GameManager _gameManager;

    [Header("Behaviour")]
    [Tooltip("Pause before the opponent throws, so the turn is readable instead of instantaneous.")]
    [Min(0f)][SerializeField] private float _thinkDelay = 0.6f;

    [SerializeField] private AiSearchSettings _search = new();

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
        if (CurrentState != State.Idle) return;

        EnsureWaitingCap();

        if (_waitingCap == null)
        {
            TurnSkipped?.Invoke(this);
            return;
        }

        _thinkTimer = _thinkDelay;
        CurrentState = State.Thinking;
    }

    void Update()
    {
        if (CurrentState != State.Thinking) return;

        _thinkTimer -= Time.deltaTime;
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

        Cap playerWaitingCap = _playerThrower != null ? _playerThrower.WaitingCap : null;

        bool found = _moveSearch.TryFindBestMove(
            _tuning,
            _fieldBoundary,
            _search,
            _candidateCaps,
            playerWaitingCap,
            ResolvePlayerThrowPower(),
            out AiMove move);

        if (!found || !move.IsValid)
        {
            SkipTurn("the search found no legal landing point");
            return;
        }

        _lastMove = move;
        _hasLastMove = true;

        var request = new CapThrowRequest(
            move.Cap,
            _pool.SpawnPosition,
            CapMath.FromXZ(move.LandingPoint, 0f),
            move.Cap.Parameters.ThrowPower);

        if (!_turnResolver.TryStartThrow(request))
        {
            SkipTurn("CapTurnResolver refused the throw");
            return;
        }

        _pool.Consume();
        _waitingCap = null;
        CurrentState = State.WaitingForResolution;
    }

    void SkipTurn(string reason)
    {
        Debug.LogWarning($"[AiCapThrower] Skipping the turn: {reason}.", this);
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
    }

    void HandleBoardReset(GameManager gameManager)
    {
        if (gameManager != _gameManager) return;

        // ResetBoard destroys every registered cap, the waiting one included.
        _waitingCap = null;
        _hasLastMove = false;
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
