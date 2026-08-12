using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>One throw the AI could make: which cap, and where it lands.</summary>
public readonly struct AiMove
{
    public readonly Cap Cap;
    public readonly Vector2 LandingPoint;
    public readonly float Score;
    public readonly CapSimResult Result;

    public AiMove(Cap cap, Vector2 landingPoint, float score, in CapSimResult result)
    {
        Cap = cap;
        LandingPoint = landingPoint;
        Score = score;
        Result = result;
    }

    public bool IsValid => Cap != null;
}

/// <summary>
/// Picks the opponent's throw: generate candidate landing points, replay each one through
/// CapBoardSimulation, score the resulting board, take the best.
///
/// This is the standard shape for games with a continuous action space and a deterministic forward
/// model (Angry Birds agents, curling, carrom): sampling a handful of meaningful shots and simulating
/// them beats trying to reason about the continuous space directly. Because this game has no physics,
/// the simulation is exact rather than approximate, so the search does not need many samples to be sharp.
///
/// The search looks one move ahead. The extra-turn rule is folded into the evaluation instead:
/// a throw that knocks an enemy cap off keeps the turn, which is worth more than any board position.
/// </summary>
public sealed class AiMoveSearch
{
    /// <summary>A landing point and the score it earned, kept for gizmo drawing.</summary>
    public readonly struct ScoredPoint
    {
        public readonly Vector2 Point;
        public readonly float Score;

        public ScoredPoint(Vector2 point, float score)
        {
            Point = point;
            Score = score;
        }
    }

    private static readonly float[] DefaultRingOffsets = { 0.95f, 0.75f, 0.5f };
    private static readonly Comparison<AiMove> ByScoreDescending = (a, b) => b.Score.CompareTo(a.Score);

    private readonly CapBoardSimulation _simulation = new();
    private readonly List<Vector2> _candidates = new();
    private readonly HashSet<long> _candidateKeys = new();
    private readonly List<AiMove> _ranked = new();
    private readonly List<ScoredPoint> _evaluatedPoints = new();

    private bool _checkScoringZones;

    /// <summary>Every landing point evaluated during the last search, for debug drawing.</summary>
    public IReadOnlyList<ScoredPoint> LastEvaluatedPoints => _evaluatedPoints;

    public float LastBestScore { get; private set; }
    public float LastWorstScore { get; private set; }

    /// <summary>
    /// Finds the best throw among <paramref name="candidateCaps"/>. With a deck the list holds a
    /// single cap and the search only chooses a landing point; hand-style pools can pass several and
    /// the same code picks the cap too.
    /// </summary>
    public bool TryFindBestMove(
        CapTuning tuning,
        CapFieldBoundary boundary,
        AiSearchSettings settings,
        IReadOnlyList<Cap> candidateCaps,
        Cap playerWaitingCap,
        float playerThrowPower,
        out AiMove move)
    {
        move = default;
        _ranked.Clear();
        _evaluatedPoints.Clear();
        LastBestScore = 0f;
        LastWorstScore = 0f;

        if (tuning == null || settings == null || candidateCaps == null || candidateCaps.Count == 0)
            return false;

        // The overlap test for aim-blocking zones is the only physics query in the search. Scenes
        // without such a zone — the AI scene among them — skip it entirely.
        _checkScoringZones = UnityEngine.Object.FindFirstObjectByType<ScoringZone>() != null;

        var result = new CapSimResult();

        for (int c = 0; c < candidateCaps.Count; c++)
        {
            Cap cap = candidateCaps[c];
            if (cap == null) continue;

            _simulation.Capture(tuning, boundary, CapRegistry.AllCaps, cap, playerWaitingCap);
            _simulation.SetSlammer(cap.Owner, cap.Parameters, cap.FlipEffects);

            BuildCandidates(boundary, settings, cap);

            float force = cap.Parameters.ThrowPower;

            for (int i = 0; i < _candidates.Count; i++)
            {
                Vector2 point = _candidates[i];
                _simulation.RunThrow(point, force, playerThrowPower, settings.MaxChainDepth, ref result);

                float score = Evaluate(result, settings);
                _ranked.Add(new AiMove(cap, point, score, result));
                _evaluatedPoints.Add(new ScoredPoint(point, score));
            }
        }

        if (_ranked.Count == 0) return false;

        _ranked.Sort(ByScoreDescending);
        LastBestScore = _ranked[0].Score;
        LastWorstScore = _ranked[_ranked.Count - 1].Score;

        if (settings.VerboseLog) LogTopMoves(settings);

        int choiceCount = Mathf.Clamp(settings.TopNChoices, 1, _ranked.Count);
        AiMove chosen = _ranked[choiceCount > 1 ? UnityEngine.Random.Range(0, choiceCount) : 0];

        move = ApplyAimJitter(chosen, boundary, settings);
        return true;
    }

    /// <summary>
    /// Turns a simulated board into a single number. The extra-turn rule drives the shape of it:
    /// knocking an enemy cap off means the AI throws again, so the bonus for doing so outweighs any
    /// positional term, and the exposure of its own caps barely counts on such a turn because the
    /// player never gets to act on it.
    /// </summary>
    public static float Evaluate(in CapSimResult result, AiSearchSettings settings)
    {
        bool keepsTurn = result.PlayerRemoved > 0;

        // Clearing the board wins the match outright, so it beats every other consideration.
        // Subtracting own losses only breaks ties between winning moves.
        if (keepsTurn && result.PlayerRemaining == 0)
            return settings.WinScore - result.OpponentRemoved;

        float dangerScale = keepsTurn ? settings.ExtraTurnDangerDiscount : 1f;

        return settings.KillWeight * result.PlayerRemoved
             + (keepsTurn ? settings.ExtraTurnBonus : 0f)
             - settings.SelfLossWeight * result.OpponentRemoved
             - settings.NeutralLossWeight * result.NeutralRemoved
             - settings.OwnDangerWeight * result.OpponentDanger * dangerScale
             + settings.EnemyDangerWeight * result.PlayerDanger
             - settings.OwnStackedWeight * result.OpponentStacked;
    }

    void BuildCandidates(CapFieldBoundary boundary, AiSearchSettings settings, Cap slammer)
    {
        _candidates.Clear();
        _candidateKeys.Clear();

        float slammerRadius = slammer.Parameters.Radius;
        float slammerPower = slammer.Parameters.ThrowPower;
        float[] offsets = settings.RingOffsets != null && settings.RingOffsets.Length > 0
            ? settings.RingOffsets
            : DefaultRingOffsets;

        int capCount = _simulation.Count;
        int slammerIndex = _simulation.SlammerIndex;

        for (int i = 0; i < capCount; i++)
        {
            if (i == slammerIndex) continue;

            CapOwner owner = _simulation.GetOwner(i);
            bool isEnemy = owner == CapOwner.Player;
            bool isBomb = _simulation.HasRadialEffect(i);

            bool worthTargeting = isEnemy
                || isBomb
                || (owner == CapOwner.Neutral && settings.TargetNeutralCaps)
                || (owner == CapOwner.Opponent && settings.TargetOwnCaps);
            if (!worthTargeting) continue;

            Vector2 target = _simulation.GetCapturedPosition(i);
            float combinedRadius = slammerRadius + _simulation.GetRadius(i);

            int angleCount = Mathf.Max(4, settings.RingAngles);

            // Caps that can actually be driven off in one hit are where the extra-turn rule pays out,
            // so they get twice the angular resolution and everything else gets half of it.
            if (isEnemy && boundary != null)
            {
                float edgeDistance = boundary.DistanceToEdge(target, out Vector2 nearestEdgePoint);
                bool knockable = edgeDistance < _simulation.GetMaxKnockDistance(i, slammerPower);
                angleCount = knockable ? angleCount * 2 : angleCount;

                // The single most valuable shot against this cap: hit it from the far side so it
                // travels straight at the closest edge, grazing enough to carry the full force.
                Vector2 toEdge = nearestEdgePoint - target;
                if (toEdge.sqrMagnitude > 0.000001f)
                {
                    Vector2 escape = toEdge.normalized;
                    TryAddCandidate(target - escape * (0.95f * combinedRadius), boundary, settings, slammerRadius);
                }
            }
            else if (!isEnemy)
            {
                angleCount = Mathf.Max(4, angleCount / 2);
            }

            for (int a = 0; a < angleCount; a++)
            {
                float angle = a * Mathf.PI * 2f / angleCount;
                var direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));

                for (int o = 0; o < offsets.Length; o++)
                {
                    Vector2 point = target + direction * (offsets[o] * combinedRadius);
                    TryAddCandidate(point, boundary, settings, slammerRadius);
                }
            }
        }

        AddGridCandidates(boundary, settings, slammerRadius);

        // Never leave the AI without a move: the middle of the field is always a legal, quiet throw.
        if (_candidates.Count == 0 && boundary != null)
            _candidates.Add(CapMath.ToXZ(boundary.FieldWorldBounds.center));

        // Say so rather than let a truncated sweep pass for full coverage.
        if (_candidates.Count >= settings.MaxCandidates)
        {
            Debug.LogWarning(
                $"[AiMoveSearch] Hit the MaxCandidates limit of {settings.MaxCandidates}; " +
                "later landing points were dropped. Raise the limit or lower RingAngles/GridStep.");
        }
    }

    /// <summary>
    /// A coarse sweep of the whole field. It rarely wins against a targeted shot, but it is what the
    /// AI falls back on when there is nothing to knock off and it just needs a safe place to land.
    /// </summary>
    void AddGridCandidates(CapFieldBoundary boundary, AiSearchSettings settings, float slammerRadius)
    {
        if (boundary == null || settings.GridStep <= 0f) return;

        Bounds bounds = boundary.FieldWorldBounds;
        if (bounds.size.x <= 0f || bounds.size.z <= 0f) return;

        float step = Mathf.Max(0.5f, settings.GridStep);

        for (float x = bounds.min.x + step * 0.5f; x <= bounds.max.x; x += step)
        {
            for (float z = bounds.min.z + step * 0.5f; z <= bounds.max.z; z += step)
                TryAddCandidate(new Vector2(x, z), boundary, settings, slammerRadius);
        }
    }

    bool TryAddCandidate(Vector2 point, CapFieldBoundary boundary, AiSearchSettings settings, float slammerRadius)
    {
        if (_candidates.Count >= settings.MaxCandidates) return false;
        if (!IsLandingAllowed(point, boundary, settings, slammerRadius)) return false;

        float step = Mathf.Max(0.05f, settings.DeduplicationStep);
        long key = ((long)Mathf.RoundToInt(point.x / step) << 32) ^ (uint)Mathf.RoundToInt(point.y / step);
        if (!_candidateKeys.Add(key)) return false;

        _candidates.Add(point);
        return true;
    }

    bool IsLandingAllowed(Vector2 point, CapFieldBoundary boundary, AiSearchSettings settings, float slammerRadius)
    {
        if (boundary != null && boundary.DistanceToEdge(point) < settings.LandingEdgeMargin)
            return false;

        if (_checkScoringZones && CapAimRules.IsBlockedByScoringZone(CapMath.FromXZ(point, 0f), slammerRadius))
            return false;

        return true;
    }

    /// <summary>
    /// Nudges the chosen landing point by a random offset. Applied after the choice on purpose: this
    /// is a shaky hand, not a worse plan, so the throw honestly misses what the AI aimed at.
    /// </summary>
    AiMove ApplyAimJitter(in AiMove move, CapFieldBoundary boundary, AiSearchSettings settings)
    {
        if (settings.AimJitter <= 0f) return move;

        Vector2 jittered = move.LandingPoint + UnityEngine.Random.insideUnitCircle * settings.AimJitter;
        if (boundary != null && boundary.DistanceToEdge(jittered) < settings.LandingEdgeMargin)
            return move;

        return new AiMove(move.Cap, jittered, move.Score, move.Result);
    }

    void LogTopMoves(AiSearchSettings settings)
    {
        int count = Mathf.Min(5, _ranked.Count);
        var log = new System.Text.StringBuilder();
        log.AppendLine($"[AiMoveSearch] {_ranked.Count} candidates, top {count}:");

        for (int i = 0; i < count; i++)
        {
            AiMove move = _ranked[i];
            CapSimResult r = move.Result;
            log.AppendLine(
                $"  {i + 1}. score {move.Score:F2} at {move.LandingPoint} — " +
                $"killed {r.PlayerRemoved} (extra turn: {r.PlayerRemoved > 0}), " +
                $"lost {r.OpponentRemoved}, neutral {r.NeutralRemoved}, stacked {r.OpponentStacked}, " +
                $"own danger {r.OpponentDanger:F2}, enemy danger {r.PlayerDanger:F2}, " +
                $"player caps left {r.PlayerRemaining}");
        }

        Debug.Log(log.ToString());
    }
}
