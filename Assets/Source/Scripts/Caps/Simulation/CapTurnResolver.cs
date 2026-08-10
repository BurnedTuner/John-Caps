using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Owns the runtime simulation of a throw, its physical chain and flip effects.
/// Player and AI throw controllers submit CapThrowRequest values to this shared resolver.
/// </summary>
[DisallowMultipleComponent]
public sealed class CapTurnResolver : MonoBehaviour, ICapEffectCommandExecutor
{
    public enum State { Idle, Throwing, Resolving }

    public State CurrentState { get; private set; } = State.Idle;
    public bool IsBusy => CurrentState != State.Idle;

    public event Action<Vector3, float> OnTableImpact;
    public event Action<Vector3, float, int> OnCapImpact;
    public event Action<CapTurnResolver> OnTurnFinished;

    [Header("References")]
    [SerializeField] private CapFieldBoundary _fieldBoundary;

    private CapTuning _tuning;
    private Cap _throwingCap;
    private CapEffectResolver _effectResolver;

    private int _throwId;
    private int _chainCount;
    private int _impactDepth;
    private float _settleElapsed;

    private readonly List<PendingLanding> _pendingLandings = new();
    private readonly List<CapFlipEvent> _pendingFlipEvents = new();
    private readonly List<CapPrediction> _landingHits = new();
    private readonly List<Cap> _stackTargets = new();

    struct PendingLanding
    {
        public Cap LandedCap;
        public Vector2 LandingPosition;
        public float LandingForce;
        public float RemainingDelay;
    }

    void Awake()
    {
        _tuning = CapTuning.Instance;
        _effectResolver = new CapEffectResolver(new CapRegistryEffectQuery(), this);

        if (_fieldBoundary == null)
            _fieldBoundary = FindFirstObjectByType<CapFieldBoundary>();
    }

    void Update()
    {
        switch (CurrentState)
        {
            case State.Throwing:
                UpdateThrowing();
                break;
            case State.Resolving:
                UpdateResolving();
                break;
        }
    }

    public bool TryStartThrow(in CapThrowRequest request)
    {
        if (_tuning == null)
            _tuning = CapTuning.Instance;

        if (IsBusy || request.Cap == null || _tuning == null) return false;
        if (!IsFinite(request.StartPosition) || !IsFinite(request.LandingPosition)) return false;
        if (!float.IsFinite(request.Force) || request.Force < 0f) return false;

        _throwId++;
        _chainCount = 0;
        _impactDepth = 0;
        _settleElapsed = 0f;
        _pendingLandings.Clear();
        _pendingFlipEvents.Clear();
        _landingHits.Clear();
        _stackTargets.Clear();

        _throwingCap = request.Cap;
        _throwingCap.transform.position = request.StartPosition;
        _throwingCap.SetImmutable(true);
        _throwingCap.BeginThrow(
            request.StartPosition,
            request.LandingPosition,
            request.Force,
            _tuning.FlightDuration,
            _tuning.ArcHeight);

        CurrentState = State.Throwing;
        return true;
    }

    public void ResetSimulation()
    {
        if (_throwingCap != null)
            _throwingCap.SetImmutable(false);

        _throwingCap = null;
        _chainCount = 0;
        _impactDepth = 0;
        _settleElapsed = 0f;
        _pendingLandings.Clear();
        _pendingFlipEvents.Clear();
        _landingHits.Clear();
        _stackTargets.Clear();
        CurrentState = State.Idle;
    }

    void UpdateThrowing()
    {
        if (_throwingCap == null)
        {
            CurrentState = State.Resolving;
            _settleElapsed = 0f;
            return;
        }

        _throwingCap.StepSimulation(Time.deltaTime, OnCapLanded, OnCapFlipped);

        if (_throwingCap.CurrentState == Cap.CapState.Idle)
        {
            _throwingCap.SetImmutable(false);
            _throwingCap = null;
            CurrentState = State.Resolving;
            _settleElapsed = 0f;
        }
    }

    void UpdateResolving()
    {
        float deltaTime = Time.deltaTime;

        int capCount = CapRegistry.AllCaps.Count;
        for (int i = 0; i < capCount; i++)
        {
            CapRegistry.AllCaps[i].StepSimulation(deltaTime, OnCapLanded, OnCapFlipped);
        }

        ResolvePendingFlipEffects();
        ResolvePendingLandings(deltaTime);

        bool anyBusy = _pendingLandings.Count > 0;
        if (!anyBusy)
        {
            for (int i = 0; i < CapRegistry.AllCaps.Count; i++)
            {
                if (CapRegistry.AllCaps[i].IsBusy)
                {
                    anyBusy = true;
                    break;
                }
            }
        }

        if (anyBusy)
        {
            _settleElapsed = 0f;
            return;
        }

        _settleElapsed += deltaTime;
        if (_settleElapsed >= _tuning.SettleDelay)
            FinishTurn();
    }

    void ResolvePendingLandings(float deltaTime)
    {
        for (int i = 0; i < _pendingLandings.Count;)
        {
            PendingLanding pending = _pendingLandings[i];
            if (pending.LandedCap == null)
            {
                _pendingLandings.RemoveAt(i);
                continue;
            }

            pending.RemainingDelay -= deltaTime;
            if (pending.RemainingDelay > 0f)
            {
                _pendingLandings[i] = pending;
                i++;
                continue;
            }

            _pendingLandings.RemoveAt(i);
            if (_chainCount < _tuning.MaximumChainLength)
                ResolveLanding(pending.LandedCap, pending.LandingPosition, pending.LandingForce);
        }
    }

    void FinishTurn()
    {
        _throwingCap = null;
        CurrentState = State.Idle;
        OnTurnFinished?.Invoke(this);
    }

    void OnCapLanded(Cap landedCap, Vector2 landingPosition, float landingForce)
    {
        if (landedCap != _throwingCap)
        {
            _pendingLandings.Add(new PendingLanding
            {
                LandedCap = landedCap,
                LandingPosition = landingPosition,
                LandingForce = landingForce,
                RemainingDelay = _tuning.ChainContactDelay
            });
            return;
        }

        ResolveLanding(landedCap, landingPosition, landingForce);
    }

    void OnCapFlipped(Cap flippedCap, Vector2 position, float incomingForce)
    {
        if (flippedCap != null)
            _pendingFlipEvents.Add(new CapFlipEvent(flippedCap, position, incomingForce));
    }

    void ResolvePendingFlipEffects()
    {
        if (_pendingFlipEvents.Count == 0) return;

        for (int i = 0; i < _pendingFlipEvents.Count; i++)
            _effectResolver.ResolveImmediate(_pendingFlipEvents[i]);

        _pendingFlipEvents.Clear();
    }

    bool ICapEffectCommandExecutor.TryLaunch(Cap source, Cap target, Vector2 direction, float rawForce)
    {
        if (source == null || target == null || _tuning == null) return false;

        float force = rawForce * target.Parameters.PowerConversion;
        float travelDistance = force * _tuning.ForceToTravelDistance;
        if (!float.IsFinite(force) || !float.IsFinite(travelDistance)) return false;
        if (travelDistance < _tuning.MinimumFlightLength) return false;

        return TryActivateCap(
            target,
            direction,
            force,
            travelDistance,
            source.StableId,
            source.ActivationDepthPlusOne);
    }

    void ResolveLanding(Cap landedCap, Vector2 landingPosition, float landingForce)
    {
        if (landedCap == null) return;

        float slammerRadius = landedCap.Parameters.Radius;
        _landingHits.Clear();
        _stackTargets.Clear();

        for (int i = 0; i < CapRegistry.AllCaps.Count; i++)
        {
            Cap cap = CapRegistry.AllCaps[i];
            if (cap == landedCap || !cap.CanFlip) continue;

            float combinedRadius = slammerRadius + cap.Parameters.Radius;
            float distance = Vector2.Distance(landingPosition, cap.GroundPosition);
            if (distance > combinedRadius) continue;

            float normalizedOffset = combinedRadius > 0f
                ? Mathf.Clamp01(distance / combinedRadius)
                : 0f;
            float contactFactor = cap.GetContactFactor(normalizedOffset);
            float inheritedForce = landingForce * cap.Parameters.PowerConversion;
            float travelDistance = inheritedForce * contactFactor * _tuning.ForceToTravelDistance;

            Vector2 direction = CapMath.VerticalImpactDirection(
                landingPosition,
                cap.GroundPosition,
                Vector2.up);

            if (travelDistance < _tuning.MinimumFlightLength)
            {
                _stackTargets.Add(cap);
                continue;
            }

            _landingHits.Add(new CapPrediction(
                cap,
                0,
                cap.GroundPosition,
                direction,
                inheritedForce,
                travelDistance));
        }

        for (int i = 0; i < _landingHits.Count; i++)
        {
            CapPrediction hit = _landingHits[i];
            TryActivateCap(hit.Cap, hit.Direction, hit.Force, hit.TravelDistance, -1, 0);
        }

        for (int i = 0; i < _stackTargets.Count; i++)
        {
            _stackTargets[i].AddToStack(landedCap);
        }

        Vector3 landingPosition3D = CapMath.FromXZ(landingPosition, 0f);
        bool isPeelOff = landedCap.WasPeelOff;
        landedCap.WasPeelOff = false;

        if (_landingHits.Count > 0 || _stackTargets.Count > 0 || isPeelOff)
        {
            _impactDepth++;
            OnCapImpact?.Invoke(landingPosition3D, landingForce, _impactDepth);
        }
        else if (IsLandingSupported(landedCap, landingPosition))
        {
            OnTableImpact?.Invoke(landingPosition3D, landingForce);
        }

        ApplyPush(landedCap, landingPosition);
    }

    // A cap that came down past the field never touched the table, so it makes no landing sound.
    bool IsLandingSupported(Cap landedCap, Vector2 landingPosition) =>
        _fieldBoundary == null || _fieldBoundary.Supports(landingPosition, landedCap.Parameters.Radius);

    void ApplyPush(Cap landedCap, Vector2 landingPosition)
    {
        for (int i = 0; i < CapRegistry.AllCaps.Count; i++)
        {
            Cap cap = CapRegistry.AllCaps[i];
            if (cap == landedCap || !cap.IsThrowable) continue;

            float distance = Vector2.Distance(landingPosition, cap.GroundPosition);
            if (distance > cap.Parameters.PushRadius) continue;
            if (WasDirectHit(cap)) continue;
            if (WasStackTarget(cap)) continue;

            Vector2 direction = cap.GroundPosition - landingPosition;
            if (direction.sqrMagnitude < 0.0001f)
                direction = UnityEngine.Random.insideUnitCircle.normalized;
            else
                direction.Normalize();

            float falloff = 1f - distance / cap.Parameters.PushRadius;
            cap.BeginPush(
                direction,
                cap.Parameters.PushDistance * falloff,
                cap.Parameters.PushDuration);
        }
    }

    bool WasDirectHit(Cap cap)
    {
        for (int i = 0; i < _landingHits.Count; i++)
        {
            if (_landingHits[i].Cap == cap)
                return true;
        }
        return false;
    }

    bool WasStackTarget(Cap cap)
    {
        for (int i = 0; i < _stackTargets.Count; i++)
        {
            if (_stackTargets[i] == cap)
                return true;
        }
        return false;
    }

    bool TryActivateCap(
        Cap target,
        Vector2 direction,
        float force,
        float travelDistance,
        int ignoredSourceId,
        int depth)
    {
        if (target == null || _chainCount >= _tuning.MaximumChainLength) return false;

        if (!target.BeginLaunch(
                _throwId,
                depth,
                direction,
                force,
                travelDistance,
                _tuning.ChainFlightDuration,
                ignoredSourceId))
            return false;

        _chainCount++;
        return true;
    }

    static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.x) &&
        float.IsFinite(value.y) &&
        float.IsFinite(value.z);
}