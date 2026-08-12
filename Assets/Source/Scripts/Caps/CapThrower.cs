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
    [SerializeField] private CapHand _hand;

    [Header("Ownership")]
    public CapOwner ThrowOwner = CapOwner.Player;

    [Header("Layers")]
    [Tooltip("Layer for the cap while held in hand (renders above everything).")]
    public int PlayerHandLayer = 0; // Set this in inspector to your "PlayerHand" layer index

    [Header("Cancel drag")]
    [Tooltip("If the cursor returns within this screen-pixel distance of the held cap's " +
             "original hand position, the drag is cancelled (cap returns to hand).")]
    [Min(10f)] public float CancelDragRadiusPixels = 120f;

    public State CurrentState { get; private set; } = State.Idle;
    public bool TurnInputEnabled { get; private set; } = true;
    public CapTurnResolver TurnResolver => _turnResolver;
    public CapHand Hand => _hand;

    private const int AimOverlapBufferSize = 32;

    private readonly Collider[] _aimOverlapBuffer = new Collider[AimOverlapBufferSize];
    private readonly RaycastHit[] _fieldHitBuffer = new RaycastHit[AimOverlapBufferSize];
    private readonly List<CapPrediction> _directHitSeeds = new();
    private readonly List<CapPrediction> _predictionResults = new();

    private CapTuning _tuning;
    private Vector2 _aimPoint;
    private bool _isDirectAimAllowed;
    private float _throwForce;
    private Cap _heldCap;
    private Vector3 _heldCapOriginalPos;
    private bool _cursorLeftHandArea;

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

        if (_hand == null)
            Debug.LogError("[CapThrower] CapHand is not assigned or present in the scene.", this);
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

        if (_hand == null)
            _hand = FindFirstObjectByType<CapHand>();
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
        float capRadius = _heldCap != null ? _heldCap.Parameters.Radius : 0.5f;
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

    /// <summary>
    /// Find the hand cap under the cursor. Returns the cap, or null if no cap
    /// is within CapGrabRadiusPixels of the cursor.
    /// </summary>
    Cap GetHandCapUnderCursor()
    {
        if (_hand == null || PlayerCamera == null || Mouse.current == null) return null;
        Vector2 mousePos = Mouse.current.position.ReadValue();
        return _hand.GetCapUnderScreenPosition(mousePos, PlayerCamera);
    }

    void UpdateIdle()
    {
        if (Keyboard.current?.rKey.wasPressedThisFrame == true)
        {
            RequestBoardReset();
            return;
        }

        if (ClickedOnUI() || Mouse.current == null) return;
        if (!Mouse.current.leftButton.wasPressedThisFrame) return;

        Cap cap = GetHandCapUnderCursor();
        if (cap == null) return;

        // Start dragging this cap. Capture its original hand position so we can
        // keep it there (raise in place) instead of teleporting to spawn center.
        _heldCap = cap;
        _heldCapOriginalPos = cap.transform.position;
        _cursorLeftHandArea = false;
        _aimPoint = CapMath.ToXZ(_heldCapOriginalPos);
        _isDirectAimAllowed = false;
        TrajectoryPreview?.Hide();
        cap.BeginHeld(_heldCapOriginalPos);
        CurrentState = State.Aiming;
    }

    void UpdateAiming()
    {
        if (Mouse.current == null || _heldCap == null)
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

        // Keep the held cap at its original hand position (raise in place).
        _heldCap.UpdateHeldBasePosition(_heldCapOriginalPos);

        _heldCap.StepSimulation(Time.deltaTime, null);

        if (TryGetFieldPoint(out Vector3 point, out bool isDirectAimAllowed))
        {
            _aimPoint = CapMath.ToXZ(point);
            _isDirectAimAllowed = isDirectAimAllowed;
        }
        else
        {
            _isDirectAimAllowed = false;
        }

        // Track whether the cursor has left the hand area since the drag started.
        // We only cancel-on-return if the cursor actually left first — otherwise
        // grabbing a cap that's near the spawn point would instantly cancel.
        if (!_cursorLeftHandArea && !IsCursorNearHand())
            _cursorLeftHandArea = true;

        // Cancel drag if the cursor left the hand area and then returned.
        if (_cursorLeftHandArea && IsCursorNearHand())
        {
            CancelAiming();
            return;
        }

        UpdateAimPreview();

        if (!Mouse.current.leftButton.isPressed)
            Fire();
    }

    /// <summary>
    /// Returns true if the cursor is near the held cap's original hand position
    /// (within CancelDragRadiusPixels in screen space). Uses the cap's original
    /// position — not the spawn point — because hand caps sit offset behind the
    /// spawn point, so the spawn point is the wrong reference center.
    /// </summary>
    bool IsCursorNearHand()
    {
        if (_heldCap == null || PlayerCamera == null || Mouse.current == null) return false;

        Vector3 handScreenPos = PlayerCamera.WorldToScreenPoint(_heldCapOriginalPos);
        if (handScreenPos.z < 0f) return false; // behind camera

        Vector2 mousePos = Mouse.current.position.ReadValue();
        float dist = Vector2.Distance(mousePos, new Vector2(handScreenPos.x, handScreenPos.y));
        return dist <= CancelDragRadiusPixels;
    }

    float GetDragDistance() =>
        Vector2.Distance(CapMath.ToXZ(_heldCapOriginalPos), _aimPoint);

    void UpdateAimPreview()
    {
        if (!_isDirectAimAllowed)
        {
            _directHitSeeds.Clear();
            _predictionResults.Clear();
            TrajectoryPreview?.Hide();
            return;
        }

        _throwForce = _heldCap != null ? _heldCap.Parameters.ThrowPower : 5f;
        float slammerRadius = _heldCap != null ? _heldCap.Parameters.Radius : 0.5f;

        CollectDirectHitPredictions(_aimPoint, _throwForce, slammerRadius, _directHitSeeds);

        // Use StackPeelOffPredictor: ignores PredictionDepth (shows full chain),
        // respects MaximumChainLength, and peels off stacks the same way the real
        // sim does — so ghost previews match what will actually happen.
        _predictionResults.Clear();
        StackPeelOffPredictor.Predict(
            CapRegistry.AllCaps,
            _directHitSeeds,
            _tuning,
            _predictionResults);

        TrajectoryPreview?.Show(
            _heldCapOriginalPos,
            _aimPoint,
            slammerRadius,
            _tuning,
            _directHitSeeds,
            _predictionResults);

        // Add transparent ghost-cap previews on top of the line-based trajectory.
        TrajectoryPreview?.ShowGhosts(_predictionResults);
    }

    void CancelAiming()
    {
        TrajectoryPreview?.Hide();

        if (_heldCap != null)
        {
            _heldCap.EndHeldToIdle();
            // Immediately override the position EndHeldToIdle→ApplyVisuals set
            // (it snaps to GroundPosition, which is stale for hand caps). Setting
            // it here avoids a one-frame snap to the wrong position before
            // CapHand.LayoutHand runs next frame.
            _heldCap.transform.position = _heldCapOriginalPos;
        }

        _heldCap = null;
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

        Cap cap = _heldCap;
        if (cap == null)
        {
            Debug.LogError("[CapThrower] No held cap to throw.", this);
            CurrentState = State.Idle;
            return;
        }

        TrajectoryPreview?.Hide();

        Vector3 startPosition = _heldCapOriginalPos;
        Vector3 landingPosition = CapMath.FromXZ(_aimPoint, 0f);

        // Reset layer back to Default (0) so it interacts with the world normally
        SetCapLayerRecursive(cap.gameObject, 0);

        // Make the cap interactable again (re-register in CapRegistry, clear immutable).
        // CapHand made it non-interactable when it was instantiated into the hand.
        if (_hand != null) _hand.ReleaseCapForThrow(cap);

        float force = cap.Parameters.ThrowPower;
        var request = new CapThrowRequest(cap, startPosition, landingPosition, force);

        if (_turnResolver.TryStartThrow(request))
        {
            // The cap has left the hand — clear its slot. The hand will
            // draw a new cap from the deck on HandleTurnFinished.
            if (_hand != null) _hand.ClearSlot(cap);
            _heldCap = null;
            CurrentState = State.WaitingForResolution;
            return;
        }

        // Throw failed (e.g. resolver busy). Cap stays in hand.
        cap.EndHeldToIdle();
        _heldCap = null;
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
            if (cap == _heldCap) continue;
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
                travelDistance,
                willLandHeads: !cap.IsHeads));
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
        // Draw a new cap from the deck to refill the empty hand slot.
        if (_hand != null) _hand.DrawFromDeck();
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
        TrajectoryPreview?.ClearGhosts(); // destroy pooled ghosts — caps are being recreated
        _directHitSeeds.Clear();
        _predictionResults.Clear();
        _isDirectAimAllowed = false;
        CurrentState = State.Idle;

        // Reset the hand: destroy all hand caps, restore deck from template, refill.
        if (_hand != null) _hand.ResetHand();
    }
}