using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Why the match ended. Passed to MatchFinished listeners so the UI can show
/// a reason string (e.g., "Reached kill target", "All enemy caps knocked off",
/// "No caps left to throw").
/// </summary>
public enum MatchEndReason
{
    /// <summary>Unknown / not set. Should never be used in practice.</summary>
    Unknown = 0,
    /// <summary>The winning side reached the kill target.</summary>
    KillTarget,
    /// <summary>The winning side knocked all enemy caps off the field.</summary>
    EnemyWipedOut,
    /// <summary>The losing side ran out of caps to throw (hand + deck empty).</summary>
    NoCapsLeft,
    /// <summary>Both sides ran out of caps — no winner.</summary>
    Draw,
}

/// <summary>Details of an extra turn earned by knocking enemy caps off the field.</summary>
public readonly struct ExtraTurnInfo
{
    /// <summary>The side that keeps the turn.</summary>
    public readonly CapOwner Owner;

    /// <summary>How many enemy caps this turn drove off the field.</summary>
    public readonly int CapsKnockedOff;

    /// <summary>Length of the streak the extra turn continues, the upcoming turn included.</summary>
    public readonly int ConsecutiveTurns;

    public ExtraTurnInfo(CapOwner owner, int capsKnockedOff, int consecutiveTurns)
    {
        Owner = owner;
        CapsKnockedOff = capsKnockedOff;
        ConsecutiveTurns = consecutiveTurns;
    }
}

/// <summary>
/// Decides whose turn it is.
///
/// The core rule: a side that drives at least one enemy cap off the field during its turn keeps the
/// turn and throws again. Only a turn that knocks nothing off hands the board over. That makes a run
/// of knockouts the strongest thing either side can do, and the AI evaluation is weighted to match.
///
/// The match ends in one of two ways, and both are decided by what is standing on the table:
/// a side loses the moment its last cap leaves the field, and a side that has just played its last cap
/// loses if the other one still has anything standing — see WinnerByExhaustion. Neither is a count
/// comparison: being behind on caps is not losing, having none is.
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

    [Tooltip("End the match once a side has no caps left on the field. " +
             "Turn it off for a sandbox that never ends.")]
    [SerializeField] private bool _endMatchWhenSideWipedOut = true;

    [Tooltip("If > 0, the match ends when a side has knocked out this many enemy caps. " +
             "0 = disabled (use the wipe-out condition instead). " +
             "Player kills = opponent losses, and vice versa — they are the same metric.")]
    [Min(0)] [SerializeField] private int _killTarget;

    [Tooltip("A cap buried under another one still counts as being on the field, so covering a side's " +
             "last cap does not beat it. Turn it off to treat a buried cap as out of the game.")]
    [SerializeField] private bool _stackedCapsCountAsOnField = true;

    [Tooltip("Cap on how many turns in a row one side may take. 0 = unlimited, which is the rule as written.")]
    [Min(0)][SerializeField] private int _maxConsecutiveTurns;

    [Tooltip("Seconds a turn may take to settle before the board is reset as a recovery.")]
    [Min(1f)][SerializeField] private float _turnTimeout = 15f;

    [Header("Debug")]
    [SerializeField] private bool _logTurns;

    public CapOwner CurrentTurn { get; private set; } = CapOwner.Neutral;
    public TurnPhase CurrentPhase { get; private set; } = TurnPhase.Idle;

    /// <summary>
    /// Winner once the match is over. Neutral while it is still running, and in a sandbox scene that ran
    /// out of caps with only one side played. A real match always names a side.
    /// </summary>
    public CapOwner Winner { get; private set; } = CapOwner.Neutral;

    /// <summary>How many turns in a row the current side has taken, this one included.</summary>
    public int ConsecutiveTurns { get; private set; }

    public event System.Action<CapOwner> TurnStarted;
    public event System.Action<CapOwner, MatchEndReason> MatchFinished;

    /// <summary>
    /// Raised just before an extra turn starts, i.e. when a side knocked enemy caps off and therefore
    /// keeps the board. Fires only once the extra turn is certain — after the streak limit and the
    /// "can this side still throw" checks have had their say.
    /// </summary>
    public event System.Action<ExtraTurnInfo> ExtraTurnEarned;

    /// <summary>
    /// Raised when a cap is knocked off the field. Parameters:
    /// (killedOwner, playerKills, opponentKills).
    /// killedOwner is the owner of the cap that was knocked off.
    /// playerKills = opponent caps knocked off by the player (= opponent losses).
    /// opponentKills = player caps knocked off by the opponent (= player losses).
    /// UI binds to this to update kill count displays.
    /// </summary>
    public event System.Action<CapOwner, int, int> KillCountChanged;

    // Knockouts are tallied from CapFieldBoundary.OnCapLeftField as they happen, not by diffing cap
    // counts before and after the turn. A diff cannot tell a knockout from a cap that merely landed
    // in a stack, because Cap.AddToStack also removes it from CapRegistry.
    private int _playerCapsLostThisTurn;
    private int _opponentCapsLostThisTurn;
    private int _neutralCapsLostThisTurn;

    // Cumulative kill counts (persist across turns, reset on board reset).
    // Player kills = opponent caps knocked off = opponent losses.
    // Opponent kills = player caps knocked off = player losses.
    // They are the SAME metric viewed from each side's perspective.
    private int _playerKills;
    private int _opponentKills;

    /// <summary>Total enemy caps the player has knocked off (= opponent losses).</summary>
    public int PlayerKills => _playerKills;
    /// <summary>Total enemy caps the opponent has knocked off (= player losses).</summary>
    public int OpponentKills => _opponentKills;
    /// <summary>The kill target (0 = disabled).</summary>
    public int KillTarget => _killTarget;

    private float _turnElapsed;
    private bool _restartRequested;

    // The opponent is started from Update rather than from inside BeginTurn. AiCapThrower can decide
    // it cannot act and raise TurnSkipped straight away, which comes back here as another BeginTurn;
    // going through Update keeps that from happening while the first one is still on the stack.
    private bool _opponentTurnPending;

    // A side can only be wiped out if it ever had caps on the field. Tracked as it happens rather than
    // snapshotted at the first turn, because a side may start empty and put its first cap down later.
    private bool _playerEverHadCaps;
    private bool _opponentEverHadCaps;

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

        if (_fieldBoundary == null)
        {
            Debug.LogError("[TurnController] CapFieldBoundary is not assigned or present in the scene. " +
                           "Without it no knockout can be detected, so turns will never repeat.", this);
        }

        BeginTurn(_firstTurn, isRepeat: false);
    }

    /// <summary>
    /// The AI weighs a move by how the rules reward it, so it has to be told what they are. Anything
    /// it is not told, it assumes, and then it plays a different game than the one on screen.
    /// Refreshed every turn so that flipping a rule in the inspector mid-play takes effect.
    /// </summary>
    void PushRulesToOpponent() =>
        _opponentThrower?.ApplyTurnRules(_neutralGrantsExtraTurn, _stackedCapsCountAsOnField);

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

        if (_fieldBoundary != null)
        {
            _fieldBoundary.OnCapLeftField -= HandleCapLeftField;
            _fieldBoundary.OnCapLeftField += HandleCapLeftField;
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

        if (_fieldBoundary != null)
            _fieldBoundary.OnCapLeftField -= HandleCapLeftField;
    }

    void Update()
    {
        // The restart is deferred out of the OnBoardReset callback so it does not depend on whether
        // the throwers happened to be notified before or after this component.
        if (_restartRequested)
        {
            _restartRequested = false;
            _opponentTurnPending = false;
            BeginTurn(_firstTurn, isRepeat: false);
            return;
        }

        if (_opponentTurnPending)
        {
            _opponentTurnPending = false;
            if (CurrentPhase == TurnPhase.OpponentTurn)
                _opponentThrower?.BeginTurn();
        }

        UpdateWatchdog();
    }

    /// <summary>
    /// Starts a turn for a side. <paramref name="isRepeat"/> marks the extra turn earned by a knockout;
    /// <paramref name="capsKnockedOff"/> is how many enemy caps earned it, and both only travel as far
    /// as the streak counter and the ExtraTurnEarned event.
    /// </summary>
    void BeginTurn(CapOwner owner, bool isRepeat, int capsKnockedOff = 0)
    {
        if (CurrentPhase == TurnPhase.MatchOver) return;

        if (isRepeat && _maxConsecutiveTurns > 0 && ConsecutiveTurns >= _maxConsecutiveTurns)
        {
            owner = Other(owner);
            isRepeat = false;
        }

        if (IsMatch)
        {
            // The match is decided the moment either side has played its last cap: it can no longer
            // answer what the other one left standing, and letting the other throw on alone is not a
            // game. Which of the two ran out is what decides it — see WinnerByExhaustion.
            // Checked in this order so that if both are out at once, the side whose turn it would have
            // been counts as the one that ran out, which is the one that just failed to act.
            if (!CanThrow(owner))
            {
                FinishMatch(WinnerByExhaustion(owner), MatchEndReason.NoCapsLeft);
                return;
            }

            if (!CanThrow(Other(owner)))
            {
                FinishMatch(WinnerByExhaustion(Other(owner)), MatchEndReason.NoCapsLeft);
                return;
            }
        }
        else if (!CanThrow(owner))
        {
            // Only one side is played at all, which is a sandbox rather than a match: the board simply
            // goes to whoever is there to throw.
            CapOwner other = Other(owner);
            if (!CanThrow(other))
            {
                FinishMatch(CapOwner.Neutral, MatchEndReason.Draw);
                return;
            }

            owner = other;
            isRepeat = false;
        }

        PushRulesToOpponent();

        ConsecutiveTurns = isRepeat ? ConsecutiveTurns + 1 : 1;
        CurrentTurn = owner;
        _turnElapsed = 0f;

        _playerCapsLostThisTurn = 0;
        _opponentCapsLostThisTurn = 0;
        _neutralCapsLostThisTurn = 0;

        // Marks both sides as being in the game so an empty board can be told from one not yet played on.
        MarkSidesInPlay();

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

        // Announced before the turn itself, so a listener can explain why the board is not changing hands.
        if (isRepeat)
            ExtraTurnEarned?.Invoke(new ExtraTurnInfo(owner, capsKnockedOff, ConsecutiveTurns));

        TurnStarted?.Invoke(owner);

        // Deferred to Update rather than started here: AiCapThrower can refuse the turn synchronously,
        // which lands back in BeginTurn through HandleOpponentSkipped.
        _opponentTurnPending = owner == CapOwner.Opponent;
    }

    void HandleTurnFinished(CapTurnResolver resolver)
    {
        if (resolver != _turnResolver) return;
        if (CurrentPhase != TurnPhase.PlayerTurn && CurrentPhase != TurnPhase.OpponentTurn) return;

        FieldCounts counts = CountCapsOnField();

        int enemyRemoved = CurrentTurn == CapOwner.Player
            ? _opponentCapsLostThisTurn
            : _playerCapsLostThisTurn;

        if (_neutralGrantsExtraTurn)
            enemyRemoved += _neutralCapsLostThisTurn;

        if (TryFinishMatch(counts)) return;

        bool keepsTurn = enemyRemoved > 0;
        BeginTurn(keepsTurn ? CurrentTurn : Other(CurrentTurn), keepsTurn, enemyRemoved);
    }

    void HandleCapLeftField(Cap cap)
    {
        if (cap == null) return;

        switch (cap.Owner)
        {
            case CapOwner.Player:
                _playerCapsLostThisTurn++;
                _opponentKills++;
                break;
            case CapOwner.Opponent:
                _opponentCapsLostThisTurn++;
                _playerKills++;
                break;
            default:
                _neutralCapsLostThisTurn++;
                break;
        }

        // Record the lost cap for RunManager (if a run is active).
        if (RunManager.Instance != null && RunManager.Instance.IsRunActive)
        {
            RunManager.Instance.RecordCapLost(cap);
        }

        // Notify UI listeners.
        KillCountChanged?.Invoke(cap.Owner, _playerKills, _opponentKills);

        // Check kill-target win condition: player reached the kill target.
        if (_killTarget > 0 && _playerKills >= _killTarget && CurrentPhase != TurnPhase.MatchOver)
        {
            FinishMatch(CapOwner.Player, MatchEndReason.KillTarget);
            return;
        }

        // Check kill-target win condition: opponent reached the kill target.
        if (_killTarget > 0 && _opponentKills >= _killTarget && CurrentPhase != TurnPhase.MatchOver)
        {
            FinishMatch(CapOwner.Opponent, MatchEndReason.KillTarget);
            return;
        }
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
        _playerEverHadCaps = false;
        _opponentEverHadCaps = false;
        _playerCapsLostThisTurn = 0;
        _opponentCapsLostThisTurn = 0;
        _neutralCapsLostThisTurn = 0;
        _playerKills = 0;
        _opponentKills = 0;
        _turnElapsed = 0f;
        _opponentTurnPending = false;
        _restartRequested = true;
    }

    /// <summary>
    /// Who wins when <paramref name="exhausted"/> has just played its last cap.
    ///
    /// Running out is only fatal if the other side still has a cap standing: those caps can no longer
    /// be answered, so the side that ran out has lost. If the other side has nothing left on the table
    /// either, then it is the one that was beaten — whatever it still holds in reserve is of no use,
    /// because the board is what the match is played on.
    ///
    /// Deliberately not a count comparison: being behind on caps is not losing, having none is.
    /// </summary>
    CapOwner WinnerByExhaustion(CapOwner exhausted)
    {
        FieldCounts counts = CountCapsOnField();
        CapOwner other = Other(exhausted);
        int otherCapsOnField = other == CapOwner.Player ? counts.Player : counts.Opponent;

        return otherCapsOnField > 0 ? other : exhausted;
    }

    bool TryFinishMatch(in FieldCounts counts)
    {
        if (!_endMatchWhenSideWipedOut) return false;

        // WIN conditions take PRIORITY over LOSE conditions.
        // The side whose turn just ended (CurrentTurn) is the one that ACTED.
        // Check if THEY knocked out all enemy caps (WIN) before checking if
        // they themselves are wiped out or out of caps (LOSE).
        //
        // This handles the scenario: player throws their last cap, it knocks
        // off all enemy caps AND the player's cap also flies off. Both sides
        // are wiped out, but the player should WIN because they knocked out
        // all enemies. Without this priority, the player-wipeout check would
        // fire first and the opponent would win — wrong.
        //
        // Kill-count win condition is already handled in HandleCapLeftField
        // (runs before TryFinishMatch), so it has the highest priority.

        CapOwner other = Other(CurrentTurn);
        int otherCapsOnField = other == CapOwner.Player ? counts.Player : counts.Opponent;
        int currentCapsOnField = CurrentTurn == CapOwner.Player ? counts.Player : counts.Opponent;

        // 1. WIN: the other side is wiped out (current player knocked off all enemies).
        if (IsWipedOut(other, otherCapsOnField))
        {
            FinishMatch(CurrentTurn, MatchEndReason.EnemyWipedOut);
            return true;
        }

        // 2. LOSE: the current player is wiped out (no caps on field).
        if (IsWipedOut(CurrentTurn, currentCapsOnField))
        {
            FinishMatch(other, MatchEndReason.EnemyWipedOut);
            return true;
        }

        // 3. LOSE: no caps left in hand AND deck (exhaustion).
        // Only checked for the side whose turn just ended — the other side
        // hasn't had a chance to throw yet, so it's not their turn to lose.
        if (!SideHasCapsLeft(CurrentTurn))
        {
            FinishMatch(other, MatchEndReason.NoCapsLeft);
            return true;
        }

        return false;
    }

    /// <summary>
    /// True if the given side has at least one cap available to throw — either
    /// a cap currently held, in the hand, or remaining in the deck. Used by the
    /// "no caps left" lose condition in <see cref="TryFinishMatch"/>.
    /// </summary>
    bool SideHasCapsLeft(CapOwner owner)
    {
        if (owner == CapOwner.Player)
            return _playerThrower != null && _playerThrower.HasCapToThrow;
        if (owner == CapOwner.Opponent)
            return _opponentThrower != null && _opponentThrower.HasCapToThrow;
        return true; // Neutral never loses by exhaustion.
    }

    /// <summary>
    /// A side is beaten the moment its last cap leaves the field, whatever it still has in reserve.
    /// The one exception is a side that has never put a cap down: it has not started, not lost, which
    /// is what keeps a sandbox board of neutral caps from ending the match on the first throw.
    /// </summary>
    bool IsWipedOut(CapOwner owner, int capsOnField)
    {
        if (capsOnField > 0) return false;

        return owner == CapOwner.Player ? _playerEverHadCaps : _opponentEverHadCaps;
    }

    void FinishMatch(CapOwner winner, MatchEndReason reason = MatchEndReason.Unknown)
    {
        Winner = winner;
        CurrentPhase = TurnPhase.MatchOver;
        _playerThrower?.SetTurnInputEnabled(false);

        Debug.Log($"[TurnController] Match over. Winner: {winner}. Reason: {reason}. Press R to reset the board.", this);
        MatchFinished?.Invoke(winner, reason);

        // Notify RunManager if a run is active.
        if (RunManager.Instance != null && RunManager.Instance.IsRunActive)
        {
            RunManager.Instance.OnMatchFinished(winner, reason);
        }
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
            if (cap.IsParked) continue;
            if (_fieldBoundary != null && !_fieldBoundary.Supports(cap.GroundPosition, 0f)) continue;

            Count(cap, ref counts);

            // Caps riding in a stack are unregistered but still very much on the table, so by default
            // a side whose last cap got covered has not lost.
            if (!_stackedCapsCountAsOnField) continue;

            IReadOnlyList<Cap> stacked = cap.StackedAbove;
            for (int s = 0; s < stacked.Count; s++)
                Count(stacked[s], ref counts);
        }

        _playerEverHadCaps |= counts.Player > 0;
        _opponentEverHadCaps |= counts.Opponent > 0;

        return counts;
    }

    /// <summary>
    /// Runs the count purely for its side effect on _playerEverHadCaps / _opponentEverHadCaps, which
    /// is what tells a side that has lost its last cap apart from one that has not played yet.
    /// </summary>
    void MarkSidesInPlay() => CountCapsOnField();

    static void Count(Cap cap, ref FieldCounts counts)
    {
        if (cap == null) return;

        switch (cap.Owner)
        {
            case CapOwner.Player: counts.Player++; break;
            case CapOwner.Opponent: counts.Opponent++; break;
            default: counts.Neutral++; break;
        }
    }

    /// <summary>
    /// True when both sides are actually played. A scene with only one thrower is a sandbox, and
    /// running out of caps there must not be read as losing.
    /// </summary>
    bool IsMatch => _playerThrower != null && _opponentThrower != null;

    bool CanThrow(CapOwner owner) => owner == CapOwner.Player
        ? _playerThrower != null && _playerThrower.HasCapToThrow
        : _opponentThrower != null && _opponentThrower.HasCapToThrow;

    /// <summary>
    /// Recovers from a turn that goes nowhere. Resetting the board is the one path that already puts
    /// the resolver, both throwers and the registry back into a known state.
    /// </summary>
    void UpdateWatchdog()
    {
        if (CurrentPhase != TurnPhase.PlayerTurn && CurrentPhase != TurnPhase.OpponentTurn) return;

        if (!IsTurnStalled())
        {
            _turnElapsed = 0f;
            return;
        }

        _turnElapsed += Time.deltaTime;
        if (_turnElapsed < _turnTimeout) return;

        _turnElapsed = 0f;
        Debug.LogWarning($"[TurnController] The {CurrentTurn} turn made no progress within " +
                         $"{_turnTimeout} s. Recovering.", this);

        if (_gameManager != null)
        {
            _gameManager.ResetBoard();
            return;
        }

        // ResetSimulation puts the resolver back to idle without raising OnTurnFinished, so nobody
        // else would ever pick the loop back up — the throwers and this controller have to be told.
        _turnResolver?.ResetSimulation();
        RestartTurnAfterRecovery();
    }

    /// <summary>
    /// A turn is stalled when it is nobody's move to make: the resolver has been chewing on the same
    /// throw for too long, or it is the opponent's turn and the opponent is not acting on it.
    /// The player's turn is never stalled on its own — it waits for input, for as long as it likes.
    /// </summary>
    bool IsTurnStalled()
    {
        if (_turnResolver != null && _turnResolver.IsBusy) return true;
        if (CurrentPhase != TurnPhase.OpponentTurn) return false;

        return !_opponentTurnPending
            && (_opponentThrower == null || _opponentThrower.CurrentState == AiCapThrower.State.Idle);
    }

    void RestartTurnAfterRecovery()
    {
        _playerThrower?.AbortTurn();
        _opponentThrower?.AbortTurn();

        _opponentTurnPending = false;
        CurrentPhase = TurnPhase.Idle;
        BeginTurn(CurrentTurn, isRepeat: false);
    }

    static CapOwner Other(CapOwner owner) =>
        owner == CapOwner.Player ? CapOwner.Opponent : CapOwner.Player;
}
