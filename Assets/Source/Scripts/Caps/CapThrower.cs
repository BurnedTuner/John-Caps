using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// Handles player input, aiming and preview, then submits a throw to CapTurnResolver.
/// </summary>
public sealed class CapThrower : MonoBehaviour
{
    public enum State { Idle, Aiming, WaitingForResolution }

    [Header("References")]
    public Camera PlayerCamera;
    public LayerMask FieldMask = ~0;
    public Cap CapPrefab;
    public TrajectoryPreview TrajectoryPreview;
    [SerializeField] private CapTurnResolver _turnResolver;
    [SerializeField] private GameManager _gameManager;

    [Header("Ownership")]
    public CapOwner ThrowOwner = CapOwner.Player;

    [Header("Layers")]
    [Tooltip("Layer for the cap while held in hand (renders above everything).")]
    public int PlayerHandLayer = 0; // Set this in inspector to your "PlayerHand" layer index
    public State CurrentState { get; private set; } = State.Idle;
    public bool TurnInputEnabled { get; private set; } = true;
    public CapTurnResolver TurnResolver => _turnResolver;

    private const int AimOverlapBufferSize = 32;

    private readonly Collider[] _aimOverlapBuffer = new Collider[AimOverlapBufferSize];
    private readonly RaycastHit[] _fieldHitBuffer = new RaycastHit[AimOverlapBufferSize];
    private readonly List<CapPrediction> _directHitSeeds = new();
    private readonly List<CapPrediction> _predictionResults = new();

    private CapTuning _tuning;
    private Vector2 _aimPoint;
    private bool _isDirectAimAllowed;
    private float _throwForce;
    private Cap _waitingCap;

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
            Debug.LogError("[CapThrower] CapTurnResolver is not assigned or present in the scene.", this);

        SpawnWaitingCap();
    }

    void OnDisable()
    {
        Unsubscribe();
    }

    void ResolveReferences()
    {
        if (_tuning == null)
            _tuning = CapTuning.Instance;

        if (_turnResolver == null)
            _turnResolver = FindFirstObjectByType<CapTurnResolver>();

        if (_gameManager == null)
            _gameManager = FindFirstObjectByType<GameManager>();
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

    void SpawnWaitingCap()
    {
        if (_waitingCap != null || CapPrefab == null || _tuning == null) return;

        Vector3 spawnPosition = _tuning.SpawnPosition;
        _waitingCap = CapFactory.Create(
            CapPrefab,
            CapMath.ToXZ(spawnPosition),
            isHeads: true,
            ThrowOwner);

        if (_waitingCap != null)
        {
            _waitingCap.transform.position = spawnPosition;
            SetCapLayerRecursive(_waitingCap.gameObject, PlayerHandLayer);
        }
    }

    void Update()
    {
        if (!TurnInputEnabled)
        {
            if (CurrentState == State.Idle && Keyboard.current?.rKey.wasPressedThisFrame == true)
                RequestBoardReset();
            return;
        }

        switch (CurrentState)
        {
            case State.Idle:
                if (_turnResolver == null || !_turnResolver.IsBusy)
                    UpdateIdle();
                break;
            case State.Aiming:
                UpdateAiming();
                break;
        }
    }

    static bool ClickedOnUI() =>
        EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

    bool TryGetFieldPoint(out Vector3 point, out bool isDirectAimAllowed)
    {
        point = default;
        isDirectAimAllowed = false;

        if (PlayerCamera == null || Mouse.current == null) return false;

        Vector2 mousePosition = Mouse.current.position.ReadValue();
        Ray ray = PlayerCamera.ScreenPointToRay(mousePosition);

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

        if (!foundField) return false;

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

    bool IsCursorOverWaitingCap()
    {
        if (_waitingCap == null || PlayerCamera == null || Mouse.current == null) return false;

        Vector3 screenPosition = PlayerCamera.WorldToScreenPoint(_waitingCap.transform.position);
        if (screenPosition.z < 0f) return false;

        Vector2 mousePosition = Mouse.current.position.ReadValue();
        float screenDistance = Vector2.Distance(
            mousePosition,
            new Vector2(screenPosition.x, screenPosition.y));

        return screenDistance <= _tuning.CapGrabRadiusPixels;
    }

    void UpdateIdle()
    {
        if (Keyboard.current?.rKey.wasPressedThisFrame == true)
        {
            RequestBoardReset();
            return;
        }

        if (_waitingCap != null && _tuning.SpawnPoint != null)
        {
            _waitingCap.transform.position = _tuning.SpawnPosition;
        }

        if (ClickedOnUI() || Mouse.current == null) return;
        if (!Mouse.current.leftButton.wasPressedThisFrame) return;
        if (!IsCursorOverWaitingCap()) return;

        _aimPoint = CapMath.ToXZ(_tuning.SpawnPosition);
        _isDirectAimAllowed = false;
        TrajectoryPreview?.Hide();
        _waitingCap?.BeginHeld(_tuning.SpawnPosition);
        CurrentState = State.Aiming;
    }

    void UpdateAiming()
    {
        if (Mouse.current == null)
        {
            CancelAiming();
            return;
        }

        if (Mouse.current.rightButton.wasPressedThisFrame ||
            Keyboard.current?.escapeKey.wasPressedThisFrame == true)
        {
            CancelAiming();
            return;
        }

        if (_waitingCap != null && _tuning.SpawnPoint != null)
        {
            _waitingCap.UpdateHeldBasePosition(_tuning.SpawnPosition);
        }

        _waitingCap?.StepSimulation(Time.deltaTime, null);

        if (TryGetFieldPoint(out Vector3 point, out bool isDirectAimAllowed))
        {
            _aimPoint = CapMath.ToXZ(point);
            _isDirectAimAllowed = isDirectAimAllowed;
        }
        else
        {
            _isDirectAimAllowed = false;
        }

        UpdateAimPreview();

        if (!Mouse.current.leftButton.isPressed)
            Fire();
    }

    float GetDragDistance() =>
        Vector2.Distance(CapMath.ToXZ(_tuning.SpawnPosition), _aimPoint);

    void UpdateAimPreview()
    {
        if (!_isDirectAimAllowed)
        {
            _directHitSeeds.Clear();
            _predictionResults.Clear();
            TrajectoryPreview?.Hide();
            return;
        }

        _throwForce = _waitingCap != null ? _waitingCap.Parameters.ThrowPower : 5f;
        float slammerRadius = _waitingCap != null ? _waitingCap.Parameters.Radius : 0.5f;

        CollectDirectHitPredictions(_aimPoint, _throwForce, slammerRadius, _directHitSeeds);

        _predictionResults.Clear();
        if (_tuning.PredictionDepth > 0)
        {
            ChainPredictor.Predict(
                CapRegistry.AllCaps,
                _directHitSeeds,
                _tuning,
                _tuning.PredictionDepth,
                _predictionResults);
        }

        TrajectoryPreview?.Show(
            _tuning.SpawnPosition,
            _aimPoint,
            slammerRadius,
            _tuning,
            _directHitSeeds,
            _predictionResults);
    }

    void CancelAiming()
    {
        TrajectoryPreview?.Hide();

        if (_waitingCap != null)
        {
            _waitingCap.EndHeldToIdle();
            _waitingCap.transform.position = _tuning.SpawnPosition;
        }

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
        if (!_isDirectAimAllowed || GetDragDistance() < _tuning.MinimumDragDistance)
        {
            CancelAiming();
            return;
        }

        if (_turnResolver == null)
        {
            Debug.LogError("[CapThrower] Cannot throw without a CapTurnResolver.", this);
            CancelAiming();
            return;
        }

        TrajectoryPreview?.Hide();

        Vector3 startPosition = _tuning.SpawnPosition;
        Vector3 landingPosition = CapMath.FromXZ(_aimPoint, 0f);
        Cap cap = _waitingCap;

        if (cap == null)
        {
            cap = CapFactory.Create(
                CapPrefab,
                CapMath.ToXZ(startPosition),
                isHeads: true,
                ThrowOwner);
        }

        if (cap == null)
        {
            Debug.LogError("[CapThrower] Failed to create a cap for the throw.", this);
            CurrentState = State.Idle;
            return;
        }

        // Reset layer back to Default (0) so it interacts with the world normally
        SetCapLayerRecursive(cap.gameObject, 0);

        float force = cap.Parameters.ThrowPower;
        var request = new CapThrowRequest(cap, startPosition, landingPosition, force);

        if (_turnResolver.TryStartThrow(request))
        {
            _waitingCap = null;
            CurrentState = State.WaitingForResolution;
            return;
        }

        _waitingCap = cap;
        _waitingCap.EndHeldToIdle();
        _waitingCap.transform.position = startPosition;
        CurrentState = State.Idle;
    }

    void CollectDirectHitPredictions(
        Vector2 landingPoint,
        float throwForce,
        float slammerRadius,
        List<CapPrediction> results)
    {
        results.Clear();

        for (int i = 0; i < CapRegistry.AllCaps.Count; i++)
        {
            Cap cap = CapRegistry.AllCaps[i];
            if (cap == _waitingCap) continue;
            if (!cap.CanFlip) continue;

            float combinedRadius = slammerRadius + cap.Parameters.Radius;
            float distance = Vector2.Distance(landingPoint, cap.GroundPosition);
            if (distance > combinedRadius) continue;

            float normalizedOffset = combinedRadius > 0f
                ? Mathf.Clamp01(distance / combinedRadius)
                : 0f;
            float contactFactor = cap.GetContactFactor(normalizedOffset);
            float inheritedForce = throwForce * cap.Parameters.PowerConversion;
            float travelDistance = inheritedForce * contactFactor * _tuning.ForceToTravelDistance;
            if (travelDistance < _tuning.MinimumFlightLength) continue;

            Vector2 direction = CapMath.VerticalImpactDirection(
                landingPoint,
                cap.GroundPosition,
                Vector2.up);

            results.Add(new CapPrediction(
                cap,
                0,
                cap.GroundPosition,
                direction,
                inheritedForce,
                travelDistance));
        }
    }

    void SetCapLayerRecursive(GameObject obj, int layer)
    {
        obj.layer = layer;
        foreach (Transform child in obj.transform)
        {
            if (child != null)
                SetCapLayerRecursive(child.gameObject, layer);
        }
    }

    void HandleTurnFinished(CapTurnResolver resolver)
    {
        if (resolver != _turnResolver || CurrentState != State.WaitingForResolution) return;

        CurrentState = State.Idle;
        SpawnWaitingCap();
    }

    void RequestBoardReset()
    {
        ResolveReferences();
        if (_gameManager != null)
            _gameManager.ResetBoard();
        else
            Debug.LogWarning("[CapThrower] Cannot reset the board without a GameManager.", this);
    }

    void HandleBoardReset(GameManager gameManager)
    {
        if (gameManager != _gameManager) return;

        TrajectoryPreview?.Hide();
        _waitingCap = null;
        _directHitSeeds.Clear();
        _predictionResults.Clear();
        _isDirectAimAllowed = false;
        CurrentState = State.Idle;
        SpawnWaitingCap();
    }
}