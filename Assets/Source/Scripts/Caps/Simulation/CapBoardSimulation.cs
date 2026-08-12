using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Outcome of simulating a single throw on a captured board.
/// Removed = the cap ended up off the field. Stacked = the cap landed too softly and was absorbed
/// into another one, which the engine treats as leaving the registry.
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
}

/// <summary>
/// A headless copy of the throw resolution rules, used by the AI to try a move before committing to it.
/// The game has no physics — CapTurnResolver moves caps analytically on the XZ plane — so this
/// simulation can reproduce a turn exactly instead of approximating it.
///
/// The board is captured once per turn into flat arrays, then RunThrow replays a candidate throw
/// against a scratch copy. Capture/RunThrow allocate nothing once the arrays are large enough.
///
/// The formulas here MUST stay identical to CapTurnResolver.ResolveLanding and
/// CapEffectResolver.ExecuteRadialLaunch — those are the authority, this is the mirror.
///
/// Modelled faithfully:
/// - the impact formula (combined radius, contact factor, power conversion, minimum flight length);
/// - chain propagation wave by wave, matching the engine's ChainContactDelay ordering;
/// - caps leaving the field: they stop being valid targets but their own queued landing still resolves,
///   exactly like the engine, which unregisters the cap yet keeps the pending landing alive;
/// - caps that are mid-flight cannot be hit (Cap.CanFlip is false while Flying), tracked per wave;
/// - radial flip effects such as the bomb, read straight off the prefab via
///   CapFlipEffect.TryGetRadialLaunch, firing before the landings of the same wave.
///
/// Deliberately not modelled:
/// - stacks beyond "the cap is absorbed and out of play", since stacks are out of scope for now;
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
    }

    /// <summary>Per-cap state that a candidate throw mutates.</summary>
    struct CapRuntime
    {
        public Vector2 Position;
        public bool IsHeads;
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

    private CapProfile[] _profiles = new CapProfile[64];
    private CapRuntime[] _baseline = new CapRuntime[64];
    private CapRuntime[] _runtime = new CapRuntime[64];

    private readonly Queue<Landing> _queue = new();
    private readonly List<Landing> _wave = new();
    private readonly List<Hit> _hits = new();
    private readonly List<int> _stackTargets = new();

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
    /// Snapshots every cap that is actually in play. Caps that are busy, stacked or sitting outside
    /// the field are skipped — that last rule is what keeps the throwers' waiting caps, which are
    /// registered but parked at a spawn point, out of the simulation.
    /// </summary>
    public void Capture(
        CapTuning tuning,
        CapFieldBoundary boundary,
        IReadOnlyList<Cap> caps,
        Cap ignoredA,
        Cap ignoredB)
    {
        _tuning = tuning;
        _boundary = boundary;
        _boardCount = 0;
        _slammerIndex = -1;

        if (caps == null) return;

        for (int i = 0; i < caps.Count; i++)
        {
            Cap cap = caps[i];
            if (cap == null || cap == ignoredA || cap == ignoredB) continue;
            if (!cap.CanFlip) continue;
            if (boundary != null && !boundary.Supports(cap.GroundPosition, 0f)) continue;

            EnsureCapacity(_boardCount + 2);
            _profiles[_boardCount] = BuildProfile(cap.Owner, cap.Parameters, cap.FlipEffects);
            _baseline[_boardCount] = new CapRuntime
            {
                Position = cap.GroundPosition,
                IsHeads = cap.IsHeads,
                IsOnField = true,
                IsStacked = false,
                LaunchedGeneration = -1
            };
            _boardCount++;
        }

        _activeCount = _boardCount;
    }

    /// <summary>
    /// Registers the cap about to be thrown. Parameters and effects are read from a prefab or a live
    /// cap; no instance has to exist on the board yet.
    /// </summary>
    public void SetSlammer(CapOwner owner, CapParameters parameters, CapFlipEffect[] effects)
    {
        EnsureCapacity(_boardCount + 1);
        _slammerIndex = _boardCount;
        _profiles[_slammerIndex] = BuildProfile(owner, parameters, effects);
        _activeCount = _boardCount + 1;
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

        if (_slammerIndex >= 0)
        {
            // The thrown cap arrives face up and does not flip on landing: Cap.StepThrow reports the
            // landing without touching IsHeads, so a thrown bomb does not detonate on arrival.
            _runtime[_slammerIndex] = new CapRuntime
            {
                Position = landingPoint,
                IsHeads = true,
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

        _hits.Clear();
        _stackTargets.Clear();

        for (int i = 0; i < _activeCount; i++)
        {
            if (i == source) continue;
            if (!CanBeHit(i, landing.Generation)) continue;

            float combinedRadius = slammerRadius + _profiles[i].Radius;
            float distance = Vector2.Distance(landing.Position, _runtime[i].Position);
            if (distance > combinedRadius) continue;

            float normalizedOffset = combinedRadius > 0f
                ? Mathf.Clamp01(distance / combinedRadius)
                : 0f;
            float contactFactor = Mathf.Lerp(
                _profiles[i].CenterContactFactor,
                _profiles[i].EdgeContactFactor,
                normalizedOffset);
            float inheritedForce = landing.Force * _profiles[i].PowerConversion;
            float travelDistance = inheritedForce * contactFactor * _tuning.ForceToTravelDistance;

            Vector2 direction = CapMath.VerticalImpactDirection(
                landing.Position,
                _runtime[i].Position,
                Vector2.up);

            if (travelDistance < _tuning.MinimumFlightLength)
            {
                _stackTargets.Add(i);
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
        if (_stackTargets.Count > 0)
            _runtime[source].IsStacked = true;
    }

    /// <summary>Mirror of CapEffectResolver.ExecuteRadialLaunch driven by RadialLaunchCommand.</summary>
    void ApplyRadialLaunch(in Landing landing)
    {
        int source = landing.Index;
        float effectRadius = _profiles[source].EffectRadius;
        float effectForce = _profiles[source].EffectForce;
        if (effectRadius <= 0f || effectForce <= 0f) return;

        float radiusSquared = effectRadius * effectRadius;

        _hits.Clear();

        for (int i = 0; i < _activeCount; i++)
        {
            if (i == source) continue;
            if (!CanBeHit(i, landing.Generation)) continue;

            Vector2 offset = _runtime[i].Position - landing.Position;
            if (offset.sqrMagnitude >= radiusSquared) continue;

            Vector2 direction = offset.sqrMagnitude > 0.000001f
                ? offset.normalized
                : Vector2.right;

            float force = effectForce * _profiles[i].PowerConversion;
            float travelDistance = force * _tuning.ForceToTravelDistance;
            if (travelDistance < _tuning.MinimumFlightLength) continue;

            _hits.Add(new Hit
            {
                Index = i,
                Direction = direction,
                Force = force,
                TravelDistance = travelDistance
            });
        }

        for (int i = 0; i < _hits.Count; i++)
        {
            Hit hit = _hits[i];
            Activate(hit.Index, hit.Direction, hit.Force, hit.TravelDistance, landing.Generation);
        }
    }

    /// <summary>Mirror of CapTurnResolver.TryActivateCap plus Cap.StepFly's move-and-flip.</summary>
    void Activate(int index, Vector2 direction, float force, float travelDistance, int generation)
    {
        if (_activations >= _tuning.MaximumChainLength) return;

        _activations++;

        Vector2 destination = _runtime[index].Position + direction * travelDistance;
        _runtime[index].Position = destination;
        _runtime[index].IsHeads = !_runtime[index].IsHeads;
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
            FiresEffect = _runtime[index].IsHeads && _profiles[index].HasRadialEffect
        });
    }

    bool CanBeHit(int index, int generation) =>
        _runtime[index].IsOnField &&
        !_runtime[index].IsStacked &&
        _runtime[index].LaunchedGeneration <= generation;

    void Tally(float playerThrowPower, ref CapSimResult result)
    {
        result = default;

        for (int i = 0; i < _activeCount; i++)
        {
            CapOwner owner = _profiles[i].Owner;

            if (!_runtime[i].IsOnField)
            {
                switch (owner)
                {
                    case CapOwner.Player: result.PlayerRemoved++; break;
                    case CapOwner.Opponent: result.OpponentRemoved++; break;
                    default: result.NeutralRemoved++; break;
                }
                continue;
            }

            // A stacked cap is neutralised, not removed: it still rests on the table and still counts
            // towards its side, so burying the last enemy cap does not win the match. Its danger is
            // zero because nothing can hit it while it rides another cap.
            if (_runtime[i].IsStacked)
            {
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

    static CapProfile BuildProfile(CapOwner owner, CapParameters parameters, CapFlipEffect[] effects)
    {
        var profile = new CapProfile
        {
            Owner = owner,
            Radius = parameters != null ? parameters.Radius : 0.5f,
            PowerConversion = parameters != null ? parameters.PowerConversion : 1f,
            CenterContactFactor = parameters != null ? parameters.CenterContactFactor : 0f,
            EdgeContactFactor = parameters != null ? parameters.EdgeContactFactor : 1f
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
