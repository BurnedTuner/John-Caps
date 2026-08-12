using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Decides whose turn it is.
///
/// The core rule: a side that drives at least one enemy cap off the field during its turn keeps the
/// turn and throws again. Only a turn that knocks nothing off hands the board over. That makes a run
/// of knockouts the strongest thing either side can do, and the AI evaluation is weighted to match.
///
/// Turn ownership is derived from the board, not from the throwers: cap counts by owner are taken
/// when the turn starts and compared once CapTurnResolver reports the board has settled.
/// Runs after the throwers so their waiting caps already exist when the first turn begins.
/// </summary>
[DefaultExecutionOrder(100)]
[DisallowMultipleComponent]
public sealed class TurnController : MonoBehaviour
{
    public enum TurnPhase { Idle, PlayerTurn, OpponentTurn, MatchOver }

    struct FieldCounts
    {
        public int Player;
        public int Opponent;
        public int Neutral;
    }

    [Header("References")]
    [SerializeField] private CapTurnResolver _turnResolver;
    [SerializeField] private CapThrower _playerThrower;
    [SerializeField] private AiCapThrower _opponentThrower;
    [SerializeField] private CapFieldBoundary _fieldBoundary;
    [SerializeField] private GameManager _gameManager;

    [Header("Rules")]
    [Tooltip("Who throws first.")]
    [SerializeField] private CapOwner _firstTurn = CapOwner.Player;

    [Tooltip("Whether knocking a neutral cap off also earns another turn. Off: only enemy caps count.")]
    [SerializeField] private bool _neutralGrantsExtraTurn;

    [Tooltip("End the match once a side that started with caps has none left on the field.")]
    [SerializeField] private bool _endMatchWhenSideWipedOut = true;

    [Tooltip("Cap on how many turns in a row one side may take. 0 = unlimited, which is the rule as written.")]
    [Min(0)][SerializeField] private int _maxConsecutiveTurns;

    [Tooltip("Seconds a turn may take to settle before the board is reset as a recovery.")]
    [Min(1f)][SerializeField] private float _turnTimeout = 15f;

    [Header("Debug")]
    [SerializeField] private bool _logTurns;

    public CapOwner CurrentTurn { get; private set; } = CapOwner.Neutral;
    public TurnPhase CurrentPhase { get; private set; } = TurnPhase.Idle;

    /// <summary>Winner once the match is over. Neutral while it is still running or if nobody could throw.</summary>
    public CapOwner Winner { get; private set; } = CapOwner.Neutral;

    /// <summary>How many turns in a row the current side has taken, this one included.</summary>
    public int ConsecutiveTurns { get; private set; }

    public event System.Action<CapOwner> TurnStarted;
    public event System.Action<CapOwner> MatchFinished;

    private FieldCounts _countsAtTurnStart;
    private FieldCounts _initialCounts;
    private bool _hasInitialCounts;
    private float _turnElapsed;
    private bool _restartRequested;

    void Awake()
    {
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
            Debug.LogError("[TurnController] CapTurnResolver is not assigned or present in the scene.", this);

        BeginTurn(_firstTurn, isRepeat: false);
    }

    void OnDisable()
    {
        Unsubscribe();
    }

    void ResolveReferences()
    {
        if (_turnResolver == null) _turnResolver = FindFirstObjectByType<CapTurnResolver>();
        if (_playerThrower == null) _playerThrower = FindFirstObjectByType<CapThrower>();
        if (_opponentThrower == null) _opponentThrower = FindFirstObjectByType<AiCapThrower>();
        if (_fieldBoundary == null) _fieldBoundary = FindFirstObjectByType<CapFieldBoundary>();
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

        if (_opponentThrower != null)
        {
            _opponentThrower.TurnSkipped -= HandleOpponentSkipped;
            _opponentThrower.TurnSkipped += HandleOpponentSkipped;
        }
    }

    void Unsubscribe()
    {
        if (_turnResolver != null)
            _turnResolver.OnTurnFinished -= HandleTurnFinished;

        if (_gameManager != null)
            _gameManager.OnBoardReset -= HandleBoardReset;

        if (_opponentThrower != null)
            _opponentThrower.TurnSkipped -= HandleOpponentSkipped;
    }

    void Update()
    {
        // The restart is deferred out of the OnBoardReset callback so it does not depend on whether
        // the throwers happened to be notified before or after this component.
        if (_restartRequested)
        {
            _restartRequested = false;
            BeginTurn(_firstTurn, isRepeat: false);
            return;
        }

        UpdateWatchdog();
    }

    /// <summary>
    /// Starts a turn for a side. <paramref name="isRepeat"/> marks the extra turn earned by a knockout,
    /// which only matters for the consecutive-turn counter and its optional limit.
    /// </summary>
    void BeginTurn(CapOwner owner, bool isRepeat)
    {
        if (CurrentPhase == TurnPhase.MatchOver) return;

        if (isRepeat && _maxConsecutiveTurns > 0 && ConsecutiveTurns >= _maxConsecutiveTurns)
        {
            owner = Other(owner);
            isRepeat = false;
        }

        if (!CanThrow(owner))
        {
            CapOwner other = Other(owner);
            if (!CanThrow(other))
            {
                FinishMatch(CapOwner.Neutral);
                return;
            }

            owner = other;
            isRepeat = false;
        }

        ConsecutiveTurns = isRepeat ? ConsecutiveTurns + 1 : 1;
        CurrentTurn = owner;
        _countsAtTurnStart = CountCapsOnField();
        _turnElapsed = 0f;

        if (!_hasInitialCounts)
        {
            _initialCounts = _countsAtTurnStart;
            _hasInitialCounts = true;
        }

        if (owner == CapOwner.Player)
        {
            CurrentPhase = TurnPhase.PlayerTurn;
            _playerThrower?.SetTurnInputEnabled(true);
        }
        else
        {
            CurrentPhase = TurnPhase.OpponentTurn;
            _playerThrower?.SetTurnInputEnabled(false);
        }

        if (_logTurns)
            Debug.Log($"[TurnController] {owner} turn #{ConsecutiveTurns} in a row.", this);

        TurnStarted?.Invoke(owner);

        // Started last so a listener sees a consistent state, and so an immediate skip unwinds cleanly.
        if (owner == CapOwner.Opponent)
            _opponentThrower?.BeginTurn();
    }

    void HandleTurnFinished(CapTurnResolver resolver)
    {
        if (resolver != _turnResolver) return;
        if (CurrentPhase != TurnPhase.PlayerTurn && CurrentPhase != TurnPhase.OpponentTurn) return;

        FieldCounts counts = CountCapsOnField();

        int enemyRemoved = CurrentTurn == CapOwner.Player
            ? _countsAtTurnStart.Opponent - counts.Opponent
            : _countsAtTurnStart.Player - counts.Player;

        if (_neutralGrantsExtraTurn)
            enemyRemoved += _countsAtTurnStart.Neutral - counts.Neutral;

        if (TryFinishMatch(counts)) return;

        bool keepsTurn = enemyRemoved > 0;
        BeginTurn(keepsTurn ? CurrentTurn : Other(CurrentTurn), keepsTurn);
    }

    void HandleOpponentSkipped(AiCapThrower thrower)
    {
        if (thrower != _opponentThrower || CurrentPhase != TurnPhase.OpponentTurn) return;

        // The opponent cannot act, so the board never changes and there is no extra turn to earn.
        BeginTurn(CapOwner.Player, isRepeat: false);
    }

    void HandleBoardReset(GameManager gameManager)
    {
        if (gameManager != _gameManager) return;

        CurrentPhase = TurnPhase.Idle;
        CurrentTurn = CapOwner.Neutral;
        Winner = CapOwner.Neutral;
        ConsecutiveTurns = 0;
        _hasInitialCounts = false;
        _turnElapsed = 0f;
        _restartRequested = true;
    }

    bool TryFinishMatch(in FieldCounts counts)
    {
        if (!_endMatchWhenSideWipedOut) return false;

        // A side that never had caps to begin with cannot be wiped out — that is a sandbox setup,
        // not a finished match.
        if (_initialCounts.Player > 0 && counts.Player == 0)
        {
            FinishMatch(CapOwner.Opponent);
            return true;
        }

        bool opponentIsOut = counts.Opponent == 0
            && (_opponentThrower == null || !_opponentThrower.HasCapToThrow);

        if (_initialCounts.Opponent > 0 && opponentIsOut)
        {
            FinishMatch(CapOwner.Player);
            return true;
        }

        return false;
    }

    void FinishMatch(CapOwner winner)
    {
        Winner = winner;
        CurrentPhase = TurnPhase.MatchOver;
        _playerThrower?.SetTurnInputEnabled(false);

        Debug.Log($"[TurnController] Match over. Winner: {winner}. Press R to reset the board.", this);
        MatchFinished?.Invoke(winner);
    }

    /// <summary>
    /// Counts the caps standing on the field per owner. Caps waiting at a spawn point are registered
    /// like any other but are not in play, so both throwers' waiting caps are skipped, and so is
    /// anything sitting outside the field.
    /// </summary>
    FieldCounts CountCapsOnField()
    {
        var counts = new FieldCounts();

        Cap playerWaiting = _playerThrower != null ? _playerThrower.WaitingCap : null;
        Cap opponentWaiting = _opponentThrower != null ? _opponentThrower.WaitingCap : null;

        IReadOnlyList<Cap> caps = CapRegistry.AllCaps;
        for (int i = 0; i < caps.Count; i++)
        {
            Cap cap = caps[i];
            if (cap == null || cap == playerWaiting || cap == opponentWaiting) continue;
            if (_fieldBoundary != null && !_fieldBoundary.Supports(cap.GroundPosition, 0f)) continue;

            switch (cap.Owner)
            {
                case CapOwner.Player: counts.Player++; break;
                case CapOwner.Opponent: counts.Opponent++; break;
                default: counts.Neutral++; break;
            }
        }

        return counts;
    }

    bool CanThrow(CapOwner owner) => owner == CapOwner.Player
        ? _playerThrower != null
        : _opponentThrower != null && _opponentThrower.HasCapToThrow;

    /// <summary>
    /// Recovers from a turn that never settles. Resetting the board is the one path that already puts
    /// the resolver, both throwers and the registry back into a known state.
    /// </summary>
    void UpdateWatchdog()
    {
        if (CurrentPhase != TurnPhase.PlayerTurn && CurrentPhase != TurnPhase.OpponentTurn) return;

        if (_turnResolver == null || !_turnResolver.IsBusy)
        {
            _turnElapsed = 0f;
            return;
        }

        _turnElapsed += Time.deltaTime;
        if (_turnElapsed < _turnTimeout) return;

        _turnElapsed = 0f;
        Debug.LogWarning($"[TurnController] The {CurrentTurn} turn did not settle within " +
                         $"{_turnTimeout} s. Resetting the board.", this);

        if (_gameManager != null)
            _gameManager.ResetBoard();
        else
            _turnResolver.ResetSimulation();
    }

    static CapOwner Other(CapOwner owner) =>
        owner == CapOwner.Player ? CapOwner.Opponent : CapOwner.Player;
}
