using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Outcome of simulating a single throw on a captured board.
/// Removed = the cap ended up off the field. Stacked = the cap landed too softly and was absorbed
/// into another one, or it was already riding one when the board was captured.
/// Danger is the summed exposure of the caps still standing (see CapBoardSimulation.ComputeDanger).
/// </summary>
public struct CapSimResult
{
    public int PlayerRemoved;
    public int OpponentRemoved;
    public int NeutralRemoved;

    public int PlayerRemaining;
    public int OpponentRemaining;
    public int NeutralRemaining;

    public int PlayerStacked;
    public int OpponentStacked;

    public float PlayerDanger;
    public float OpponentDanger;

    /// <summary>Danger of the AI's own caps BEFORE the throw. Used to compute danger delta.</summary>
    public float OpponentDangerBefore;

    /// <summary>Danger of the player's caps BEFORE the throw. Used to compute danger delta.</summary>
    public float PlayerDangerBefore;
}

/// <summary>
/// A headless copy of the throw resolution rules, used by the AI to try a move before committing to it.
/// The game has no physics — CapTurnResolver moves caps analytically on the XZ plane — so this
/// simulation can reproduce a turn exactly instead of approximating it.
///
/// The board is captured once per turn into flat arrays, then RunThrow replays a candidate throw
/// against a scratch copy. Capture/RunThrow allocate nothing once the arrays are large enough.
///
/// The impact formulas themselves live in CapImpact and are shared with CapTurnResolver, which is
/// what keeps this mirror from drifting away from the engine it mirrors. Everything below is about
/// ordering and bookkeeping, not about the feel of a hit.
///
/// Modelled faithfully:
/// - chain propagation wave by wave, matching the engine's ChainContactDelay ordering;
/// - caps leaving the field: they stop being valid targets but their own queued landing still resolves,
///   exactly like the engine, which unregisters the cap yet keeps the pending landing alive;
/// - caps that are mid-flight cannot be hit (Cap.CanFlip is false while Flying), tracked per wave;
/// - radial flip effects such as the bomb, read straight off the prefab via
///   CapFlipEffect.TryGetRadialLaunch, firing before the landings of the same wave;
/// - caps riding in a stack: they are unregistered from CapRegistry, so Cap.StackedAbove is the only
///   way to see them, and they still belong to their side. They are never targets and never fly.
///
/// Deliberately not modelled:
/// - stack peel-off (Cap.HandleStackPeelOff). Once the base of a tower is launched the engine takes
///   the tower apart cap by cap; the simulation leaves its riders in play rather than guess, so the
///   search never claims a win it cannot deliver;
/// - Cap.BeginPush, the soft shove applied to near misses (PushRadius is 0 on every current prefab);
/// - the exact frame on which two simultaneous landings resolve.
/// </summary>
public sealed class CapBoardSimulation
{
    /// <summary>Per-cap data that cannot change during a turn.</summary>
    struct CapProfile
    {
        public CapOwner Owner;
        public float Radius;
        public float PowerConversion;
        public float CenterContactFactor;
        public float EdgeContactFactor;
        public bool HasRadialEffect;
        public float EffectRadius;
        public float EffectForce;
        public RadialEffectType EffectType;

        /// <summary>Index of the cap this one rides on, or -1 when it stands on the table itself.</summary>
        public int BaseIndex;
    }

    enum RadialEffectType
    {
        Push,
        Flip,
    }

    /// <summary>Per-cap state that a candidate throw mutates.</summary>
    struct CapRuntime
    {
        public Vector2 Position;
        public bool IsFace;
        public bool IsOnField;
        public bool IsStacked;

        /// <summary>
        /// Wave in which the cap was launched, i.e. the wave it lands in. While the simulation is
        /// resolving an earlier wave the cap is still in the air and cannot be hit, which is how
        /// Cap.CanFlip behaves for a Flying cap. -1 means the cap has never been launched.
        /// </summary>
        public int LaunchedGeneration;
    }

    struct Landing
    {
        public int Index;
        public Vector2 Position;
        public float Force;
        public int Generation;
        public bool FiresEffect;
    }

    struct Hit
    {
        public int Index;
        public Vector2 Direction;
        public float Force;
        public float TravelDistance;
    }

    private CapTuning _tuning;
    private CapFieldBoundary _boundary;
    private bool _stackedCapsCountAsOnField = true;

    private CapProfile[] _profiles = new CapProfile[64];
    private CapRuntime[] _baseline = new CapRuntime[64];
    private CapRuntime[] _runtime = new CapRuntime[64];

    private readonly Queue<Landing> _queue = new();
    private readonly List<Landing> _wave = new();
    private readonly List<Hit> _hits = new();

    private int _boardCount;
    private int _slammerIndex = -1;
    private int _activeCount;
    private int _activations;

    /// <summary>Caps on the captured board, the hypothetical thrown cap included once SetSlammer ran.</summary>
    public int Count => _activeCount;

    /// <summary>Index of the hypothetical thrown cap, or -1 when SetSlammer has not been called.</summary>
    public int SlammerIndex => _slammerIndex;

    public CapOwner GetOwner(int index) => _profiles[index].Owner;
    public float GetRadius(int index) => _profiles[index].Radius;
    public bool HasRadialEffect(int index) => _profiles[index].HasRadialEffect;

    /// <summary>True for a cap that rides on top of another one and therefore cannot be aimed at.</summary>
    public bool IsRider(int index) => _profiles[index].BaseIndex >= 0;

    /// <summary>Position the cap had when the board was captured, before any candidate throw.</summary>
    public Vector2 GetCapturedPosition(int index) => _baseline[index].Position;

    /// <summary>
    /// Longest distance one hit of the given force can drive this cap: a grazing contact pushes the
    /// contact factor all the way to EdgeContactFactor and passes the whole converted force into travel.
    /// A cap standing closer than this to the edge can be knocked off in a single throw.
    /// </summary>
    public float GetMaxKnockDistance(int index, float throwPower)
    {
        if (_tuning == null) return 0f;

        return throwPower
            * _profiles[index].PowerConversion
            * _profiles[index].EdgeContactFactor
            * _tuning.ForceToTravelDistance;
    }

    /// <summary>
    /// Snapshots every cap that is actually in play, riders in stacks included.
    /// Parked caps are skipped: they are registered like any other cap but are waiting at a thrower's
    /// spawn point rather than standing on the board, which is also why the cap about to be thrown
    /// must be parked when this runs — otherwise SetSlammer would add it a second time.
    /// <paramref name="stackedCapsCountAsOnField"/> must mirror the matching TurnController setting,
    /// or the search will disagree with the game about who has already lost. It decides both whether
    /// riders are taken onto the board at all and how a cap buried during the throw is counted.
    /// </summary>
    public void Capture(
        CapTuning tuning,
        CapFieldBoundary boundary,
        IReadOnlyList<Cap> caps,
        bool stackedCapsCountAsOnField)
    {
        _tuning = tuning;
        _boundary = boundary;
        _stackedCapsCountAsOnField = stackedCapsCountAsOnField;
        _boardCount = 0;
        _slammerIndex = -1;

        if (caps == null)
        {
            _activeCount = 0;
            return;
        }

        for (int i = 0; i < caps.Count; i++)
        {
            Cap cap = caps[i];
            if (cap == null || cap.IsParked) continue;
            if (!cap.CanFlip) continue;

            int baseIndex = _boardCount;
            AddCap(cap, cap.GroundPosition, -1);

            // With the rule turned off a buried cap is out of the game, and a cap that was already
            // buried before this turn was already out — taking it onto the board would make every
            // candidate throw look as though it had knocked it off.
            if (!stackedCapsCountAsOnField) continue;

            // Riders are unregistered from CapRegistry while stacked, so this list is the only way to
            // see them. Their position is only ever used for counting, never for a hit test, so the
            // base's position is a good enough stand-in.
            IReadOnlyList<Cap> riders = cap.StackedAbove;
            for (int s = 0; s < riders.Count; s++)
            {
                if (riders[s] == null) continue;
                AddCap(riders[s], cap.GroundPosition, baseIndex);
            }
        }

        _activeCount = _boardCount;
    }

    void AddCap(Cap cap, Vector2 position, int baseIndex)
    {
        EnsureCapacity(_boardCount + 2);

        _profiles[_boardCount] = BuildProfile(cap.Owner, cap.Parameters, cap.FlipEffects, baseIndex);
        _baseline[_boardCount] = new CapRuntime
        {
            Position = position,
            IsFace = cap.IsFace,
            IsOnField = true,
            IsStacked = baseIndex >= 0,
            LaunchedGeneration = -1
        };
        _boardCount++;
    }

    /// <summary>
    /// Registers the cap about to be thrown. Parameters and effects are read from a prefab or a live
    /// cap; no instance has to exist on the board yet.
    /// </summary>
    public void SetSlammer(CapOwner owner, CapParameters parameters, CapFlipEffect[] effects)
    {
        EnsureCapacity(_boardCount + 1);
        _slammerIndex = _boardCount;
        _profiles[_slammerIndex] = BuildProfile(owner, parameters, effects, -1);

        // RunThrow fills this in properly once it knows where the cap is aimed. Written here so the
        // entry can never be read back as leftovers from an earlier turn.
        _baseline[_slammerIndex] = new CapRuntime
        {
            IsFace = true,
            IsOnField = true,
            LaunchedGeneration = -1
        };

        _activeCount = _boardCount + 1;
    }

    /// <summary>
    /// Counts the board as it actually stands right now, using the same rules RunThrow reports with.
    /// Comparing this against the result the search predicted is the only way to notice that the
    /// mirror has drifted away from CapTurnResolver.
    /// </summary>
    public void CaptureAndTally(
        CapTuning tuning,
        CapFieldBoundary boundary,
        IReadOnlyList<Cap> caps,
        bool stackedCapsCountAsOnField,
        float playerThrowPower,
        ref CapSimResult result)
    {
        Capture(tuning, boundary, caps, stackedCapsCountAsOnField);

        for (int i = 0; i < _boardCount; i++)
            _runtime[i] = _baseline[i];

        Tally(playerThrowPower, ref result);
    }

    /// <summary>
    /// Replays one throw against a fresh copy of the captured board and reports what it leaves behind.
    /// <paramref name="playerThrowPower"/> is the force the opponent can bring to bear next turn and
    /// only feeds the danger metric.
    /// <paramref name="maxChainDepth"/> limits how far the chain is followed: 0 runs it to the end,
    /// 1 stops after the caps the throw hits directly, and so on.
    /// </summary>
    public void RunThrow(
        Vector2 landingPoint,
        float force,
        float playerThrowPower,
        int maxChainDepth,
        ref CapSimResult result)
    {
        if (_tuning == null)
        {
            result = default;
            return;
        }

        for (int i = 0; i < _boardCount; i++)
            _runtime[i] = _baseline[i];

        _activations = 0;
        _queue.Clear();

        // Capture pre-throw danger so the AI can reward moves that reduce danger
        // (protect own caps) or increase enemy danger (push enemy caps to edge).
        Tally(playerThrowPower, ref result);
        result.OpponentDangerBefore = result.OpponentDanger;
        result.PlayerDangerBefore = result.PlayerDanger;

        if (_slammerIndex >= 0)
        {
            // The thrown cap arrives face up and does not flip on landing: Cap.StepThrow reports the
            // landing without touching IsFace, so a thrown bomb does not detonate on arrival.
            _runtime[_slammerIndex] = new CapRuntime
            {
                Position = landingPoint,
                IsFace = true,
                IsOnField = true,
                IsStacked = false,
                LaunchedGeneration = -1
            };

            _queue.Enqueue(new Landing
            {
                Index = _slammerIndex,
                Position = landingPoint,
                Force = force,
                Generation = 0,
                FiresEffect = false
            });
        }

        ResolveQueue(maxChainDepth);
        Tally(playerThrowPower, ref result);
    }

    void ResolveQueue(int maxChainDepth)
    {
        while (_queue.Count > 0)
        {
            int generation = _queue.Peek().Generation;

            // Generation 0 is the throw itself, so a depth of 1 resolves only the direct hits. The
            // caps launched by the last resolved wave have already moved and may already be off the
            // field — they are simply not followed any further.
            if (maxChainDepth > 0 && generation >= maxChainDepth) break;

            _wave.Clear();
            while (_queue.Count > 0 && _queue.Peek().Generation == generation)
                _wave.Add(_queue.Dequeue());

            // Flip effects run before the landings of their own wave: the engine drains flip events
            // the frame a cap touches down, while its landing waits out ChainContactDelay first.
            for (int i = 0; i < _wave.Count; i++)
            {
                if (_wave[i].FiresEffect)
                    ApplyRadialLaunch(_wave[i]);
            }

            for (int i = 0; i < _wave.Count; i++)
            {
                if (_activations >= _tuning.MaximumChainLength) break;
                ResolveLanding(_wave[i]);
            }
        }
    }

    /// <summary>Mirror of CapTurnResolver.ResolveLanding.</summary>
    void ResolveLanding(in Landing landing)
    {
        int source = landing.Index;
        float slammerRadius = _profiles[source].Radius;
        bool absorbed = false;

        _hits.Clear();

        for (int i = 0; i < _activeCount; i++)
        {
            if (i == source) continue;
            if (!CanBeHit(i, landing.Generation)) continue;

            if (!CapImpact.TryResolveHit(
                    slammerRadius,
                    ToImpactTarget(i),
                    landing.Position,
                    _runtime[i].Position,
                    landing.Force,
                    _tuning,
                    out Vector2 direction,
                    out float inheritedForce,
                    out float travelDistance,
                    out bool stacks))
                continue;

            if (stacks)
            {
                absorbed = true;
                continue;
            }

            _hits.Add(new Hit
            {
                Index = i,
                Direction = direction,
                Force = inheritedForce,
                TravelDistance = travelDistance
            });
        }

        for (int i = 0; i < _hits.Count; i++)
        {
            Hit hit = _hits[i];
            Activate(hit.Index, hit.Direction, hit.Force, hit.TravelDistance, landing.Generation);
        }

        // Cap.AddToStack puts the cap that just landed on top of the target and unregisters it,
        // so it is the landed cap that leaves play, not the one it landed on.
        if (absorbed)
            _runtime[source].IsStacked = true;
    }

    /// <summary>Mirror of CapEffectResolver.ExecuteRadialPush/ExecuteRadialFlip.
    /// Dispatches based on EffectType: Push (bomb) or Flip (flipper).</summary>
    void ApplyRadialLaunch(in Landing landing)
    {
        int source = landing.Index;
        float effectRadius = _profiles[source].EffectRadius;
        float effectForce = _profiles[source].EffectForce;
        RadialEffectType effectType = _profiles[source].EffectType;
        if (effectRadius <= 0f) return;

        float radiusSquared = effectRadius * effectRadius;

        _hits.Clear();

        for (int i = 0; i < _activeCount; i++)
        {
            if (i == source) continue;
            if (!CanBeHit(i, landing.Generation)) continue;

            Vector2 offset = _runtime[i].Position - landing.Position;
            if (offset.sqrMagnitude >= radiusSquared) continue;

            if (effectType == RadialEffectType.Flip)
            {
                // Flipper: flip in place, no movement. Direction/force not used.
                _hits.Add(new Hit
                {
                    Index = i,
                    Direction = CapImpact.RadialDirection(landing.Position, _runtime[i].Position),
                    Force = 0f,
                    TravelDistance = 0f
                });
            }
            else
            {
                // Bomb (push): resolve launch force to travel distance.
                if (effectForce <= 0f) continue;
                if (!CapImpact.TryResolveLaunch(
                        ToImpactTarget(i),
                        effectForce,
                        _tuning,
                        out float force,
                        out float travelDistance))
                    continue;

                _hits.Add(new Hit
                {
                    Index = i,
                    Direction = CapImpact.RadialDirection(landing.Position, _runtime[i].Position),
                    Force = force,
                    TravelDistance = travelDistance
                });
            }
        }

        for (int i = 0; i < _hits.Count; i++)
        {
            Hit hit = _hits[i];
            if (effectType == RadialEffectType.Flip)
            {
                // Flip in place: toggle IsFace, no movement, no chain reaction.
                ActivateFlip(hit.Index);
            }
            else
            {
                // Push: move without flipping.
                ActivatePush(hit.Index, hit.Direction, hit.TravelDistance);
            }
        }
    }

    /// <summary>
    /// Flips a cap in place (toggle IsFace) without moving it. Does NOT queue
    /// a chain-reaction landing — the flipper doesn't cause chain reactions
    /// (caps don't move, so they can't hit other caps).
    /// </summary>
    void ActivateFlip(int index)
    {
        _runtime[index].IsFace = !_runtime[index].IsFace;
        // NOTE: no position change, no landing queued.
    }

    /// <summary>
    /// Moves a cap without flipping it (push, not launch). Does NOT queue a
    /// chain-reaction landing — pushed caps don't trigger chain reactions
    /// (only collision-based chain pushes, which aren't modelled here).
    /// </summary>
    void ActivatePush(int index, Vector2 direction, float travelDistance)
    {
        Vector2 destination = _runtime[index].Position + direction * travelDistance;
        _runtime[index].Position = destination;
        // NOTE: IsFace is NOT toggled — push doesn't flip.
        _runtime[index].LaunchedGeneration = -1; // not launched, so can be hit again

        _runtime[index].IsOnField = _boundary == null || _boundary.Supports(destination, 0f);
        // NOTE: no landing queued — push doesn't trigger chain reactions.
    }

    /// <summary>Mirror of CapTurnResolver.TryActivateCap plus Cap.StepFly's move-and-flip.</summary>
    void Activate(int index, Vector2 direction, float force, float travelDistance, int generation)
    {
        if (_activations >= _tuning.MaximumChainLength) return;

        _activations++;

        Vector2 destination = _runtime[index].Position + direction * travelDistance;
        _runtime[index].Position = destination;
        _runtime[index].IsFace = !_runtime[index].IsFace;
        _runtime[index].LaunchedGeneration = generation + 1;

        // CapFieldBoundary removes a cap once its centre leaves the field. The cap stops being a
        // valid target immediately, but the landing queued below still resolves — the engine holds a
        // direct reference to it in _pendingLandings.
        _runtime[index].IsOnField = _boundary == null || _boundary.Supports(destination, 0f);

        _queue.Enqueue(new Landing
        {
            Index = index,
            Position = destination,
            Force = force,
            Generation = generation + 1,
            FiresEffect = _runtime[index].IsFace && _profiles[index].HasRadialEffect
        });
    }

    bool CanBeHit(int index, int generation) =>
        _profiles[index].BaseIndex < 0 &&
        _runtime[index].IsOnField &&
        !_runtime[index].IsStacked &&
        _runtime[index].LaunchedGeneration <= generation;

    CapImpactTarget ToImpactTarget(int index) => new CapImpactTarget(
        _profiles[index].Radius,
        _profiles[index].PowerConversion,
        _profiles[index].CenterContactFactor,
        _profiles[index].EdgeContactFactor);

    void Tally(float playerThrowPower, ref CapSimResult result)
    {
        result = default;

        for (int i = 0; i < _activeCount; i++)
        {
            CapOwner owner = _profiles[i].Owner;

            if (!IsStillOnField(i))
            {
                AddRemoved(owner, ref result);
                continue;
            }

            if (_runtime[i].IsStacked)
            {
                // A stacked cap still rests on the table, but whether it still counts for its side is
                // a rule TurnController owns — the search mirrors whatever it is set to, otherwise the
                // two disagree about who has already lost. Its danger is zero because nothing can hit
                // it while it rides another cap.
                if (!_stackedCapsCountAsOnField)
                {
                    AddRemoved(owner, ref result);
                    continue;
                }

                switch (owner)
                {
                    case CapOwner.Player:
                        result.PlayerStacked++;
                        result.PlayerRemaining++;
                        break;
                    case CapOwner.Opponent:
                        result.OpponentStacked++;
                        result.OpponentRemaining++;
                        break;
                    default:
                        result.NeutralRemaining++;
                        break;
                }
                continue;
            }

            float danger = ComputeDanger(i, playerThrowPower);

            switch (owner)
            {
                case CapOwner.Player:
                    result.PlayerRemaining++;
                    result.PlayerDanger += danger;
                    break;
                case CapOwner.Opponent:
                    result.OpponentRemaining++;
                    result.OpponentDanger += danger;
                    break;
                default:
                    result.NeutralRemaining++;
                    break;
            }
        }
    }

    /// <summary>
    /// An untouched tower falls as a unit — CapFieldBoundary.DropCap breaks it apart and announces
    /// every cap it consisted of — so a rider shares the fate of the cap under it.
    /// Once that cap has been launched the engine peels the tower apart instead (Cap.HandleStackPeelOff),
    /// which is not modelled, and the riders are left in play rather than guessed at.
    /// </summary>
    bool IsStillOnField(int index)
    {
        if (!_runtime[index].IsOnField) return false;

        int baseIndex = _profiles[index].BaseIndex;
        if (baseIndex < 0) return true;
        if (_runtime[baseIndex].LaunchedGeneration >= 0) return true;

        return _runtime[baseIndex].IsOnField;
    }

    static void AddRemoved(CapOwner owner, ref CapSimResult result)
    {
        switch (owner)
        {
            case CapOwner.Player: result.PlayerRemoved++; break;
            case CapOwner.Opponent: result.OpponentRemoved++; break;
            default: result.NeutralRemoved++; break;
        }
    }

    /// <summary>
    /// How exposed a cap is, from 0 (safe) to 1 (knocked off by a single hit).
    ///
    /// A throw flies over the board in an arc and lands wherever the thrower aims, so every cap is
    /// always reachable — the only thing that varies is how much field is left behind it, which makes
    /// the distance to the edge the whole story.
    /// </summary>
    float ComputeDanger(int index, float playerThrowPower)
    {
        if (_boundary == null) return 0f;

        float maxKnock = GetMaxKnockDistance(index, playerThrowPower);
        if (maxKnock <= 0.01f) return 0f;

        float edgeDistance = _boundary.DistanceToEdge(_runtime[index].Position);
        return Mathf.Clamp01(1f - edgeDistance / maxKnock);
    }

    static CapProfile BuildProfile(
        CapOwner owner,
        CapParameters parameters,
        CapFlipEffect[] effects,
        int baseIndex)
    {
        var profile = new CapProfile
        {
            Owner = owner,
            Radius = parameters != null ? parameters.Radius : 0.5f,
            PowerConversion = parameters != null ? parameters.PowerConversion : 1f,
            CenterContactFactor = parameters != null ? parameters.CenterContactFactor : 0f,
            EdgeContactFactor = parameters != null ? parameters.EdgeContactFactor : 1f,
            BaseIndex = baseIndex
        };

        if (effects == null) return profile;

        for (int i = 0; i < effects.Length; i++)
        {
            CapFlipEffect effect = effects[i];
            if (effect == null || !effect.enabled) continue;
            if (!effect.TryGetRadialLaunch(out float radius, out float force)) continue;

            // Several radial effects on one cap would each fire; the strongest one dominates the
            // outcome, and no current prefab carries more than one.
            if (radius * force <= profile.EffectRadius * profile.EffectForce) continue;

            profile.HasRadialEffect = true;
            profile.EffectRadius = radius;
            profile.EffectForce = force;

            // Detect effect type: FlipperCapEffect → Flip, else (BombCapFlipEffect) → Push.
            profile.EffectType = effect is FlipperCapEffect
                ? RadialEffectType.Flip
                : RadialEffectType.Push;
        }

        return profile;
    }

    void EnsureCapacity(int required)
    {
        if (_profiles.Length >= required) return;

        int capacity = _profiles.Length;
        while (capacity < required) capacity *= 2;

        System.Array.Resize(ref _profiles, capacity);
        System.Array.Resize(ref _baseline, capacity);
        System.Array.Resize(ref _runtime, capacity);
    }
}
