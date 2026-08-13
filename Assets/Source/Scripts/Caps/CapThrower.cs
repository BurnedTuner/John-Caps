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
    public Camera HandCamera; // Used for cursor-over-hand-cap detection
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

    /// <summary>
    /// The world-space position from which throws originate. Calculated by
    /// projecting the held cap's screen position (as seen by the HandCamera)
    /// onto the PlayerCamera's near plane. This gives a world position that
    /// appears at the same screen spot from the Main Camera — so the trajectory
    /// arc and throw start from where the player sees the cap on screen.
    /// Recalculated every frame so it follows camera movement.
    /// </summary>
    Vector3 ThrowOriginPos
    {
        get
        {
            if (PlayerCamera == null || _heldCap == null)
                return _tuning.SpawnPosition;

            // Get the cap's screen position as seen by the HandCamera (or
            // PlayerCamera if no HandCamera is assigned).
            Camera projCam = HandCamera != null ? HandCamera : PlayerCamera;
            Vector3 screenPos = projCam.WorldToScreenPoint(_heldCap.transform.position);
            if (screenPos.z <= 0f)
                return PlayerCamera.transform.position;

            // Project that screen position onto the PlayerCamera at a reasonable
            // distance from the camera (not the near plane — that's too close and
            // the cap would be behind/inside the camera). Use 2 units in front
            // so the cap is visible and the throw arc looks natural.
            Vector3 playerScreen = new Vector3(screenPos.x, screenPos.y, 2f);
            return PlayerCamera.ScreenToWorldPoint(playerScreen);
        }
    }

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
    /// Find the hand cap under the cursor. Uses the HandCamera (not PlayerCamera)
    /// for screen projection, since hand caps are rendered by the HandCamera.
    /// Returns the cap, or null if no cap is within CapGrabRadiusPixels.
    /// </summary>
    Cap GetHandCapUnderCursor()
    {
        if (_hand == null || Mouse.current == null) return null;
        Camera capCam = HandCamera != null ? HandCamera : PlayerCamera;
        if (capCam == null) return null;
        Vector2 mousePos = Mouse.current.position.ReadValue();
        return _hand.GetCapUnderScreenPosition(mousePos, capCam);
    }

    void UpdateIdle()
    {
        if (Keyboard.current?.rKey.wasPressedThisFrame == true)
        {
            RequestBoardReset();
            return;
        }

        // F or RMB while hovering a hand cap flips it (toggles IsHeads).
        // RMB also starts camera orbit in CameraController, but a single click
        // won't cause visible orbit — and the flip is more useful.
        bool fPressed = Keyboard.current?.fKey.wasPressedThisFrame == true;
        bool rmbPressed = Mouse.current?.rightButton.wasPressedThisFrame == true;

        if (fPressed || rmbPressed)
        {
            Cap hoverCap = GetHandCapUnderCursor();
            if (hoverCap != null)
            {
                hoverCap.FlipInHand();
                return; // consume the input — don't start aiming
            }
        }

        if (ClickedOnUI() || Mouse.current == null) return;
        if (!Mouse.current.leftButton.wasPressedThisFrame) return;

        Cap cap = GetHandCapUnderCursor();
        if (cap == null) return;

        // Start dragging this cap. The throw will originate from the cap's
        // actual world position (its hand slot in the HandCamera overlay).
        // No screen projection — the arc and throw both start from here.
        _heldCap = cap;
        _heldCapOriginalPos = cap.transform.position; // hand position (for cancel detection)
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

        // Keep the held cap at its hand position (HandCamera overlay).
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
    /// (within CancelDragRadiusPixels in screen space). Uses the HandCamera for
    /// screen projection since hand caps are rendered by the HandCamera.
    /// </summary>
    bool IsCursorNearHand()
    {
        if (_heldCap == null || Mouse.current == null) return false;
        Camera capCam = HandCamera != null ? HandCamera : PlayerCamera;
        if (capCam == null) return false;

        Vector3 handScreenPos = capCam.WorldToScreenPoint(_heldCapOriginalPos);
        if (handScreenPos.z < 0f) return false; // behind camera

        Vector2 mousePos = Mouse.current.position.ReadValue();
        float dist = Vector2.Distance(mousePos, new Vector2(handScreenPos.x, handScreenPos.y));
        return dist <= CancelDragRadiusPixels;
    }

    float GetDragDistance() =>
        Vector2.Distance(CapMath.ToXZ(ThrowOriginPos), _aimPoint);

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
            ThrowOriginPos,
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
            // Cap returns to its hand position in the HandCamera overlay.
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

        // Capture the throw origin ONCE — this is the exact same position the
        // trajectory arc was rendering from. Don't let ThrowOriginPos recalculate
        // after this point (e.g. when _heldCap is cleared).
        Vector3 startPosition = ThrowOriginPos;
        Vector3 landingPosition = CapMath.FromXZ(_aimPoint, 0f);

        // Move the cap to the throw origin.
        cap.transform.position = startPosition;

        // Reset layer back to Default (0) so it interacts with the world normally
        // and is visible from the Main Camera.
        SetCapLayerRecursive(cap.gameObject, 0);

        // Make the cap interactable again (re-register in CapRegistry, clear immutable).
        if (_hand != null) _hand.ReleaseCapForThrow(cap);

        float force = cap.Parameters.ThrowPower;

        // Clear _heldCap BEFORE TryStartThrow so ApplyVisuals (called inside
        // BeginThrow) doesn't use the Held state's position logic.
        _heldCap = null;

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