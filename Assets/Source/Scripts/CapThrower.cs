using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;

/// <summary>
/// Orchestrates the throw flow with the NEW Input System.
/// Each cap uses its own Radius for overlap checks (supports caps of different sizes).
/// </summary>
public class CapThrower : MonoBehaviour
{
    public enum State { Idle, Aiming, Throwing, Resolving }

    [Header("References")]
    public Camera PlayerCamera;
    public LayerMask FieldMask = ~0;
    public Cap CapPrefab;
    public TrajectoryPreview TrajectoryPreview;

    [Header("Ownership")]
    public CapOwner ThrowOwner = CapOwner.Player;

    public State CurrentState { get; private set; } = State.Idle;
    public bool TurnInputEnabled { get; private set; } = true;

    public event System.Action<Vector3, float> OnTableImpact;
    public event System.Action<Vector3, float, int> OnCapImpact;
    public event System.Action<CapThrower> OnTurnFinished;
    public event System.Action<CapThrower> OnBoardReset;

    private CapTuning _tuning;
    private Vector2 _aimPoint;
    private bool _isDirectAimAllowed;
    private float _throwForce;
    private Cap _throwingCap;
    private Cap _waitingCap;

    private const int AimOverlapBufferSize = 32;
    private readonly Collider[] _aimOverlapBuffer = new Collider[AimOverlapBufferSize];
    private readonly RaycastHit[] _fieldHitBuffer = new RaycastHit[AimOverlapBufferSize];

    private int _throwId;
    private int _chainCount;
    private int _impactDepth;
    private readonly List<CapPrediction> _directHitSeeds = new();
    private readonly List<CapPrediction> _predictionResults = new();
    private readonly List<PendingLanding> _pendingLandings = new();
    private float _settleElapsed;

    struct PendingLanding
    {
        public Cap LandedCap;
        public Vector2 LandingPosition;
        public float LandingForce;
        public float RemainingDelay;
    }

    void Awake() => _tuning = CapTuning.Instance;

    void Start()
    {
        SpawnWaitingCap();
    }

    void SpawnWaitingCap()
    {
        if (_waitingCap != null) return;
        if (CapPrefab == null || _tuning == null) return;

        Vector3 spawn = _tuning.SpawnPosition;
        _waitingCap = CapFactory.Create(CapPrefab, CapMath.ToXZ(spawn), isHeads: true, ThrowOwner);
        if (_waitingCap != null)
        {
            _waitingCap.transform.position = spawn;
        }
    }

    void Update()
    {
        if (!TurnInputEnabled)
        {
            if (CurrentState == State.Idle && Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame)
                ResetBoard();
            return;
        }

        switch (CurrentState)
        {
            case State.Idle: UpdateIdle(); break;
            case State.Aiming: UpdateAiming(); break;
            case State.Throwing: UpdateThrowing(); break;
            case State.Resolving: UpdateResolving(); break;
        }
    }

    static bool ClickedOnUI() =>
        EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

    bool GetFieldPoint(out Vector3 point, out bool isDirectAimAllowed)
    {
        point = default;
        isDirectAimAllowed = false;

        if (PlayerCamera == null || Mouse.current == null) return false;

        Vector2 mousePos = Mouse.current.position.ReadValue();
        Ray ray = PlayerCamera.ScreenPointToRay(mousePos);

        int hitCount = Physics.RaycastNonAlloc(
            ray,
            _fieldHitBuffer,
            100f,
            FieldMask,
            QueryTriggerInteraction.Ignore);

        bool foundField = false;
        RaycastHit fieldHit = default;
        float nearestDistance = float.PositiveInfinity;

        for (int i = 0; i < hitCount; i++)
        {
            RaycastHit candidate = _fieldHitBuffer[i];
            if (candidate.collider == null) continue;
            if (candidate.collider.GetComponentInParent<Cap>() != null) continue;
            if (candidate.distance >= nearestDistance) continue;

            foundField = true;
            fieldHit = candidate;
            nearestDistance = candidate.distance;
        }

        if (!foundField)
            return false;

        point = fieldHit.point;
        float capRadius = _waitingCap != null ? _waitingCap.Parameters.Radius : 0.5f;
        isDirectAimAllowed = !OverlapsAimBlockingZone(point, capRadius);
        return true;
    }

    bool OverlapsAimBlockingZone(Vector3 point, float capRadius)
    {
        int hitCount = Physics.OverlapSphereNonAlloc(
            point,
            Mathf.Max(0.01f, capRadius),
            _aimOverlapBuffer,
            ~0,
            QueryTriggerInteraction.Collide);

        for (int i = 0; i < hitCount; i++)
        {
            Collider hit = _aimOverlapBuffer[i];
            if (hit == null) continue;

            ScoringZone scoringZone = hit.GetComponentInParent<ScoringZone>();
            if (scoringZone != null && scoringZone.BlocksDirectAiming)
                return true;
        }

        return false;
    }

    void UpdateIdle()
    {
        if (Keyboard.current.rKey.wasPressedThisFrame)
        {
            ResetBoard();
            return;
        }

        if (ClickedOnUI()) return;
        if (!Mouse.current.leftButton.wasPressedThisFrame) return;
        if (!GetFieldPoint(out Vector3 point, out bool isDirectAimAllowed)) return;
        if (!isDirectAimAllowed) return;

        _aimPoint = CapMath.ToXZ(point);
        _isDirectAimAllowed = true;
        CurrentState = State.Aiming;
        UpdateAimPreview();
    }

    void UpdateAiming()
    {
        if (Mouse.current.rightButton.wasPressedThisFrame || Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            CancelAiming();
            return;
        }

        if (GetFieldPoint(out Vector3 point, out bool isDirectAimAllowed))
        {
            _aimPoint = CapMath.ToXZ(point);
            _isDirectAimAllowed = isDirectAimAllowed;
        }
        else
        {
            _isDirectAimAllowed = false;
        }

        UpdateAimPreview();

        if (Mouse.current.leftButton.wasPressedThisFrame)
        {
            Fire();
            return;
        }

        if (!Mouse.current.leftButton.isPressed && GetDragDistance() >= _tuning.MinimumDragDistance)
        {
            Fire();
        }
    }

    float GetDragDistance()
    {
        Vector2 spawnXZ = CapMath.ToXZ(_tuning.SpawnPosition);
        return Vector2.Distance(spawnXZ, _aimPoint);
    }

    void UpdateAimPreview()
    {
        if (!_isDirectAimAllowed)
        {
            _directHitSeeds.Clear();
            _predictionResults.Clear();
            if (TrajectoryPreview != null) TrajectoryPreview.Hide();
            return;
        }

        _throwForce = _waitingCap != null ? _waitingCap.Parameters.ThrowPower : 5f;
        float slammerRadius = _waitingCap != null ? _waitingCap.Parameters.Radius : 0.5f;

        CollectDirectHitPredictions(_aimPoint, _throwForce, slammerRadius, _directHitSeeds);

        _predictionResults.Clear();
        if (_tuning.PredictionDepth > 0)
        {
            ChainPredictor.Predict(CapRegistry.AllCaps, _directHitSeeds, _tuning, _tuning.PredictionDepth, _predictionResults);
        }

        if (TrajectoryPreview != null)
        {
            TrajectoryPreview.Show(_tuning.SpawnPosition, _aimPoint, slammerRadius, _tuning, _directHitSeeds, _predictionResults);
        }
    }

    void CancelAiming()
    {
        if (TrajectoryPreview != null) TrajectoryPreview.Hide();
        _isDirectAimAllowed = false;
        CurrentState = State.Idle;
    }

    public void SetTurnInputEnabled(bool enabled)
    {
        TurnInputEnabled = enabled;
        if (!enabled && CurrentState == State.Aiming)
            CancelAiming();
    }

    void Fire()
    {
        if (!_isDirectAimAllowed)
        {
            CancelAiming();
            return;
        }

        float dragDist = GetDragDistance();
        if (dragDist < _tuning.MinimumDragDistance)
        {
            CancelAiming();
            return;
        }

        if (TrajectoryPreview != null) TrajectoryPreview.Hide();

        _throwId++;
        _chainCount = 0;
        _impactDepth = 0;
        _pendingLandings.Clear();

        Vector3 spawn = _tuning.SpawnPosition;
        Vector3 landPos = CapMath.FromXZ(_aimPoint, 0f);

        if (_waitingCap != null)
        {
            _throwingCap = _waitingCap;
            _waitingCap = null;
            _throwingCap.transform.position = spawn;
        }
        else
        {
            _throwingCap = CapFactory.Create(CapPrefab, CapMath.ToXZ(spawn), isHeads: true, ThrowOwner);
        }

        if (_throwingCap == null)
        {
            Debug.LogError("[CapThrower] Failed to spawn throwing cap. Is CapPrefab assigned?");
            CurrentState = State.Idle;
            return;
        }

        _throwForce = _throwingCap.Parameters.ThrowPower;

        _throwingCap.SetImmutable(true);
        _throwingCap.BeginThrow(spawn, landPos, _throwForce, _tuning.FlightDuration, _tuning.ArcHeight);

        CurrentState = State.Throwing;
    }

    void UpdateThrowing()
    {
        _throwingCap.StepSimulation(Time.deltaTime, OnCapLanded);

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
        float dt = Time.deltaTime;

        for (int i = 0; i < CapRegistry.AllCaps.Count; i++)
        {
            CapRegistry.AllCaps[i].StepSimulation(dt, OnCapLanded);
        }

        for (int i = 0; i < _pendingLandings.Count;)
        {
            var pending = _pendingLandings[i];
            pending.RemainingDelay -= dt;
            if (pending.RemainingDelay > 0f)
            {
                _pendingLandings[i] = pending;
                i++;
                continue;
            }
            _pendingLandings.RemoveAt(i);

            if (_chainCount >= _tuning.MaximumChainLength) continue;

            ResolveLanding(pending.LandedCap, pending.LandingPosition, pending.LandingForce);
        }

        bool anyBusy = _pendingLandings.Count > 0;
        if (!anyBusy)
        {
            for (int i = 0; i < CapRegistry.AllCaps.Count; i++)
            {
                if (CapRegistry.AllCaps[i].IsBusy) { anyBusy = true; break; }
            }
        }

        if (anyBusy) { _settleElapsed = 0f; return; }

        _settleElapsed += dt;
        if (_settleElapsed >= _tuning.SettleDelay) FinishThrow();
    }

    void FinishThrow()
    {
        _throwingCap = null;
        CurrentState = State.Idle;
        OnTurnFinished?.Invoke(this);
        SpawnWaitingCap();
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

    void ResolveLanding(Cap landedCap, Vector2 landingPosition, float landingForce)
    {
        float slammerRadius = landedCap.Parameters.Radius;

        var hits = new List<CapPrediction>();
        foreach (var cap in CapRegistry.AllCaps)
        {
            if (cap == landedCap) continue;
            if (cap.IsBusy) continue;

            float combined = slammerRadius + cap.Parameters.Radius;
            float dist = Vector2.Distance(landingPosition, cap.GroundPosition);
            if (dist > combined) continue;

            float normalizedOffset = combined > 0f ? Mathf.Clamp01(dist / combined) : 0f;
            float contactFactor = cap.GetContactFactor(normalizedOffset);
            float force = landingForce * cap.Parameters.PowerConversion * contactFactor;
            if (force < _tuning.MinimumFlightForce) continue;

            Vector2 direction = CapMath.VerticalImpactDirection(landingPosition, cap.GroundPosition, Vector2.up);
            float travel = force * _tuning.ForceToTravelDistance;
            hits.Add(new CapPrediction(cap, 0, cap.GroundPosition, direction, force, travel));
        }

        foreach (var hit in hits)
        {
            TryActivateCap(hit.Cap, hit.Direction, hit.Force, -1, 0);
        }

        Vector3 landingPos3D = CapMath.FromXZ(landingPosition, 0f);
        if (hits.Count > 0)
        {
            _impactDepth++;
            OnCapImpact?.Invoke(landingPos3D, landingForce, _impactDepth);
        }
        else 
        {
            OnTableImpact?.Invoke(landingPos3D, landingForce);
        }

        foreach (var cap in CapRegistry.AllCaps)
        {
            if (cap == landedCap) continue;
            if (cap.IsBusy) continue;

            float dist = Vector2.Distance(landingPosition, cap.GroundPosition);
            if (dist > cap.Parameters.PushRadius) continue;

            bool wasDirectHit = false;
            foreach (var hit in hits) { if (hit.Cap == cap) { wasDirectHit = true; break; } }
            if (wasDirectHit) continue;

            Vector2 dir = cap.GroundPosition - landingPosition;
            if (dir.sqrMagnitude < 0.0001f) dir = Random.insideUnitCircle.normalized;
            else dir.Normalize();

            float falloff = 1f - (dist / cap.Parameters.PushRadius);
            cap.BeginPush(dir, cap.Parameters.PushDistance * falloff, cap.Parameters.PushDuration);
        }
    }

    bool TryActivateCap(Cap target, Vector2 direction, float force, int ignoredSourceId, int depth)
    {
        if (target == null || _chainCount >= _tuning.MaximumChainLength) return false;
        if (target.BeginLaunch(_throwId, depth, direction, force, ignoredSourceId))
        {
            _chainCount++;
            return true;
        }
        return false;
    }

    void CollectDirectHitPredictions(Vector2 landingPoint, float throwForce, float slammerRadius, List<CapPrediction> results)
    {
        results.Clear();

        foreach (var cap in CapRegistry.AllCaps)
        {
            if (cap == _throwingCap) continue;
            float combined = slammerRadius + cap.Parameters.Radius;
            float dist = Vector2.Distance(landingPoint, cap.GroundPosition);
            if (dist > combined) continue;

            float normalizedOffset = combined > 0f ? Mathf.Clamp01(dist / combined) : 0f;
            float contactFactor = cap.GetContactFactor(normalizedOffset);
            float force = throwForce * cap.Parameters.PowerConversion * contactFactor;
            if (force < _tuning.MinimumFlightForce) continue;

            Vector2 direction = CapMath.VerticalImpactDirection(landingPoint, cap.GroundPosition, Vector2.up);
            float travel = force * _tuning.ForceToTravelDistance;
            results.Add(new CapPrediction(cap, 0, cap.GroundPosition, direction, force, travel));
        }
    }

    void ResetBoard()
    {
        foreach (var cap in CapRegistry.AllCaps.ToArray())
        {
            if (cap != null) Destroy(cap.gameObject);
        }
        CapRegistry.AllCaps.Clear();
        CapFactory.ResetIdCounter();

        var gm = FindFirstObjectByType<GameManager>();
        if (gm != null) gm.ScatterAmbientCaps();

        if (TrajectoryPreview != null) TrajectoryPreview.Hide();
        CurrentState = State.Idle;
        _throwingCap = null;
        _waitingCap = null;
        _pendingLandings.Clear();
        _chainCount = 0;
        _isDirectAimAllowed = false;
        _settleElapsed = 0f;

        SpawnWaitingCap();
        OnBoardReset?.Invoke(this);
    }
}
