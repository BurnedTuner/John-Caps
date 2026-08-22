using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// Handles player input, aiming and preview, then submits a throw to CapTurnResolver.
/// </summary>
public sealed class CapThrower : MonoBehaviour
{
    public enum State { Idle, Aiming, PrecisionAiming, WaitingForResolution }

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

    [Header("Precision Aim Mode (WASD nudge)")]
    [Tooltip("Base speed (world units / second) of WASD aim adjustment in precision mode. " +
             "Multiplied by the acceleration curve below, exactly like the camera WASD.")]
    [Min(0f)] public float PrecisionAimSpeed = 6f;

    [Tooltip("Acceleration curve for precision-mode WASD. X = seconds held, Y = speed multiplier 0-1. " +
             "Mirrors CameraController.accelerationCurve so the feel matches the camera WASD.")]
    public AnimationCurve PrecisionAimAccelerationCurve = AnimationCurve.EaseInOut(0f, 0f, 2f, 1f);

    public State CurrentState { get; private set; } = State.Idle;
    public bool TurnInputEnabled { get; private set; } = true;
    public CapTurnResolver TurnResolver => _turnResolver;
    public CapHand Hand => _hand;

    /// <summary>
    /// True when the player is actively aiming (regular drag OR precision mode).
    /// Used by PauseMenu to yield ESC to the thrower instead of opening the
    /// settings menu mid-aim.
    /// </summary>
    public bool IsAiming => CurrentState == State.Aiming || CurrentState == State.PrecisionAiming;

    /// <summary>
    /// The cap currently held/grabbed by the player. Returns null if no cap is held.
    /// Used by TurnController to check if a player has a cap waiting.
    /// </summary>
    public Cap WaitingCap => _heldCap;

    /// <summary>
    /// False once the player has nothing left to throw: nothing held, no cap in hand and an empty deck.
    /// Mirrors <see cref="AiCapThrower.HasCapToThrow"/> so TurnController can ask both sides the same
    /// question and end the match on whichever runs out first.
    ///
    /// The deck is counted as well as the hand, because CapHand only draws the replacement once the turn
    /// has resolved — asking in between would report a hand that is briefly one cap short of the truth,
    /// and with a hand of one that reads as the player being out of the game.
    ///
    /// A thrower with no CapHand is not deck-limited at all — that is a sandbox scene rather than a
    /// match — so it can always throw.
    /// </summary>
    public bool HasCapToThrow =>
        _heldCap != null || _hand == null || _hand.HasCapToThrow() || _hand.DeckCount > 0;

    /// <summary>
    /// The world-space position from which throws originate. Computed by
    /// projecting the cap's CAPTURED viewport position + depth (saved at click
    /// time) through PlayerCamera's CURRENT transform. This makes the throw
    /// origin camera-relative — it follows the camera as it moves — while
    /// matching the cap's exact screen position and depth at click time, so
    /// there is NO teleport when the throw begins.
    ///
    /// Why viewport + depth instead of the cap's live transform.position:
    /// If HandAnchor is a static world object (not parented to the camera),
    /// the cap's world position doesn't change when the camera moves, so the
    /// trajectory and throw origin would stay at a fixed world position
    /// instead of following the camera. By capturing the viewport (0-1,
    /// camera-relative) and depth at click time, then recomputing the world
    /// position every frame via PlayerCamera.ViewportToWorldPoint, the
    /// position automatically moves with the camera.
    /// </summary>
    Vector3 ThrowOriginPos
    {
        get
        {
            if (PlayerCamera == null || _heldCap == null || !_hasGrab)
                return _tuning.SpawnPosition;

            // Recompute from captured viewport + depth using PlayerCamera's
            // current transform. ViewportToWorldPoint = camera.position +
            // camera.forward * depth + camera.right * (vx-0.5) * width +
            // camera.up * (vy-0.5) * height. As the camera moves, this world
            // position moves with it.
            return PlayerCamera.ViewportToWorldPoint(
                new Vector3(_grabViewport.x, _grabViewport.y, _grabDepth));
        }
    }

    private const int AimOverlapBufferSize = 32;

    private readonly Collider[] _aimOverlapBuffer = new Collider[AimOverlapBufferSize];
    private readonly RaycastHit[] _fieldHitBuffer = new RaycastHit[AimOverlapBufferSize];
    private readonly List<CapPrediction> _directHitSeeds = new();
    private readonly List<CapPrediction> _predictionResults = new();
    private readonly List<CapPrediction> _fullPredictions = new();
    private readonly List<CapPrediction> _continuationPredictions = new();
    private readonly List<(Vector3 center, float radius, Color color)> _bombZones = new();

    // Parallel fall-off flags. For each prediction in _fullPredictions /
    // _continuationPredictions, true means the predicted cap's EndPosition is
    // outside the field (it will fall off the table). Recolored in
    // TrajectoryPreview.Show() so the player can see at a glance which throws
    // will lose a cap.
    private readonly List<bool> _fullPredictionsFallOff = new();
    private readonly List<bool> _continuationPredictionsFallOff = new();

    private CapTuning _tuning;
    private Vector2 _aimPoint;
    private bool _isDirectAimAllowed;
    private float _throwForce;
    private Cap _heldCap;
    private Vector3 _heldCapOriginalPos;
    private bool _cursorLeftHandArea;
    private Vector2 _lastAllowedAimPoint;
    private bool _hasLastAllowedAimPoint;

    // Captured at click time: the cap's viewport position (0-1, camera-relative)
    // on the HandCamera (or PlayerCamera if no HandCamera), and the cap's depth
    // from PlayerCamera along its forward axis. Throughout aiming, we
    // recompute the cap's world position from these values via
    // PlayerCamera.ViewportToWorldPoint — this makes the cap, trajectory, and
    // throw origin follow the camera while matching the cap's click-time
    // screen position and depth (no teleport).
    private Vector3 _grabViewport;
    private float _grabDepth;
    private bool _hasGrab;

    // Precision-mode WASD nudge state. Mirrors CameraController's WASD
    // acceleration: input direction + an accel timer that drives an
    // AnimationCurve to ramp the speed from 0 to full.
    private float _precisionAccelTimer;
    private Vector2 _precisionInputDir;


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

        // Global hotkey: Q toggles precision aim mode. Works in any state
        // (Idle, Aiming, etc.) so the player can flip it mid-aim if they
        // change their mind before releasing LMB. GameSettings holds the
        // persisted state so the UI toggle and this key stay in sync.
        //
        // IMPORTANT: if the player is ALREADY in PrecisionAiming, toggling Q
        // does NOT exit the current precision session. The setting is updated
        // (so the NEXT throw won't use precision mode if toggled off), but the
        // current session continues until the player confirms (Space) or
        // cancels (ESC). This matches the user spec: "if precision mode is
        // toggled while in precision mode, we dont exit out of it, the next
        // throw will just not be in precision mode".
        if (Keyboard.current?.qKey.wasPressedThisFrame == true
            && GameSettings.Instance != null)
        {
            bool newValue = !GameSettings.Instance.PrecisionAimEnabled;
            GameSettings.Instance.SetPrecisionAimEnabled(newValue);
            // Sync the UI toggle so it visually matches. PauseMenu listens for
            // the toggle's onValueChanged event, so setting .isOn without
            // suppress_dispatch fires the listener and stays in sync with the
            // GameSettings value (which we just set — the listener is a
            // no-op redundant call). Without this, the toggle would show the
            // old state until the player opens the pause menu.
            PauseMenu pm = FindFirstObjectByType<PauseMenu>();
            if (pm != null)
                pm.SyncPrecisionAimToggle(newValue);
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
            case State.PrecisionAiming:
                UpdatePrecisionAiming();
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

        // Defender cap zones: circular no-aim zones around defender caps.
        // Blocks the player if the defender is owned by the opponent (or set to Both).
        if (CapAimRules.IsBlockedByDefenderCap(point, capRadius, ThrowOwner))
            return true;

        return false;
    }

    /// <summary>
    /// Given a line from <paramref name="from"/> (an allowed point) to
    /// <paramref name="to"/> (a restricted point), find the point on the line
    /// closest to the restricted zone boundary — the last allowed point before
    /// crossing into the zone.
    ///
    /// Uses binary search: samples the midpoint, checks if it's allowed.
    /// If allowed, search the second half; if restricted, search the first half.
    /// Converges in ~12 iterations to sub-centimeter precision.
    /// </summary>
    Vector2 ClampToZoneBoundary(Vector2 from, Vector2 to, float capRadius)
    {
        // If `from` is already restricted (shouldn't happen, but defensive),
        // return `from` as-is.
        Vector3 from3D = CapMath.FromXZ(from, 0f);
        if (OverlapsAimBlockingZone(from3D, capRadius))
            return from;

        // If `to` is somehow allowed, just use it.
        Vector3 to3D = CapMath.FromXZ(to, 0f);
        if (!OverlapsAimBlockingZone(to3D, capRadius))
            return to;

        // Binary search for the boundary.
        float lo = 0f;  // allowed
        float hi = 1f;  // restricted
        Vector2 result = from;

        for (int i = 0; i < 16; i++)
        {
            float mid = (lo + hi) * 0.5f;
            Vector2 sample = Vector2.Lerp(from, to, mid);
            Vector3 sample3D = CapMath.FromXZ(sample, 0f);

            if (OverlapsAimBlockingZone(sample3D, capRadius))
            {
                // Restricted — search first half.
                hi = mid;
            }
            else
            {
                // Allowed — search second half, keep as result.
                lo = mid;
                result = sample;
            }
        }

        return result;
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
        _hasLastAllowedAimPoint = false;

        // Capture the cap's viewport position and depth at click time.
        // Throughout aiming, ThrowOriginPos recomputes the world position
        // from these values using PlayerCamera's CURRENT transform — so the
        // cap, trajectory, and throw origin follow the camera as it moves.
        // Depth is along PlayerCamera's forward axis (what ViewportToWorldPoint
        // expects for z). Clamped to >= 0.5 to avoid degenerate near-plane
        // positions.
        Camera grabCam = HandCamera != null ? HandCamera : PlayerCamera;
        if (PlayerCamera != null && grabCam != null)
        {
            _grabViewport = grabCam.WorldToViewportPoint(cap.transform.position);
            _grabDepth = Vector3.Dot(
                cap.transform.position - PlayerCamera.transform.position,
                PlayerCamera.transform.forward);
            if (_grabDepth < 0.5f) _grabDepth = 2f;
            _hasGrab = true;
        }
        else
        {
            _hasGrab = false;
        }

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

        // Keep the held cap at its hand slot position (NOT at ThrowOriginPos).
        // This prevents the grab teleport — the cap stays where it was when
        // clicked. ThrowOriginPos (the viewport+depth projection) is used ONLY
        // for the trajectory rendering and the throw start position, NOT for
        // the cap's visual position during aiming.
        //
        // GetCapSlotPosition returns the cap's current hand slot position
        // (HandAnchor-relative), so if HandAnchor moves with the camera, the
        // cap follows. If HandAnchor is static, the cap stays at its slot.
        if (_hand != null)
        {
            Vector3 slotPos = _hand.GetCapSlotPosition(_heldCap);
            if (slotPos.sqrMagnitude > 0.0001f)
                _heldCapOriginalPos = slotPos;
        }

        _heldCap.UpdateHeldBasePosition(_heldCapOriginalPos);

        _heldCap.StepSimulation(Time.deltaTime, null);

        float capRadius = _heldCap != null ? _heldCap.Parameters.Radius : 0.5f;

        // Aim point follows the cursor exactly (legacy behavior — no acceleration,
        // no dead zone). When the cursor is over a restricted zone
        // (ScoringZone / DefenderCapEffect), the aim point freezes at the last
        // allowed position so the trajectory preview stays visible.
        if (TryGetFieldPoint(out Vector3 point, out bool isAllowed))
        {
            if (isAllowed)
            {
                _aimPoint = CapMath.ToXZ(point);
                _lastAllowedAimPoint = _aimPoint;
                _hasLastAllowedAimPoint = true;
                _isDirectAimAllowed = true;
            }
            else if (_hasLastAllowedAimPoint)
            {
                Vector2 cursorPoint = CapMath.ToXZ(point);
                _aimPoint = ClampToZoneBoundary(_lastAllowedAimPoint, cursorPoint, capRadius);
                _lastAllowedAimPoint = _aimPoint;
                _isDirectAimAllowed = true;
            }
        }

        // Clamp to zone boundaries: if the aim point entered a restricted zone,
        // clamp it back to the boundary and zero out velocity to prevent buildup.
        Vector3 aimPoint3D = CapMath.FromXZ(_aimPoint, 0f);
        if (OverlapsAimBlockingZone(aimPoint3D, capRadius))
        {
            if (_hasLastAllowedAimPoint)
            {
                _aimPoint = ClampToZoneBoundary(_lastAllowedAimPoint, _aimPoint, capRadius);
            }
        }

        // Check if the (possibly clamped) aim point is now allowed.
        aimPoint3D = CapMath.FromXZ(_aimPoint, 0f);
        if (!OverlapsAimBlockingZone(aimPoint3D, capRadius))
        {
            _lastAllowedAimPoint = _aimPoint;
            _hasLastAllowedAimPoint = true;
            _isDirectAimAllowed = true;
        }
        else
        {
            _isDirectAimAllowed = _hasLastAllowedAimPoint;
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
        {
            // If precision mode is enabled, transition to PrecisionAiming
            // instead of firing immediately. The player can nudge the aim
            // point with WASD and confirm with Space (or cancel with ESC).
            if (GameSettings.IsPrecisionAimEnabled())
            {
                EnterPrecisionAiming();
                return;
            }
            Fire();
        }
    }

    /// <summary>
    /// Transition from Aiming into PrecisionAiming. The cap stays held, the
    /// trajectory preview stays visible, but LMB is no longer required — the
    /// player can take their hand off the mouse and use WASD to nudge the aim
    /// point. Space confirms the throw, ESC cancels (and PauseMenu yields ESC
    /// to us while we're in this state).
    /// </summary>
    void EnterPrecisionAiming()
    {
        // If the drag was never valid (no allowed aim point, or drag distance
        // too short), don't enter precision mode — just fire/cancel as usual.
        // This matches the legacy Fire() guard and prevents entering a dead
        // state with no valid aim point to nudge.
        if (!_isDirectAimAllowed || GetDragDistance() < _tuning.MinimumDragDistance)
        {
            Fire();
            return;
        }

        _precisionAccelTimer = 0f;
        _precisionInputDir = Vector2.zero;
        CurrentState = State.PrecisionAiming;
    }

    /// <summary>
    /// Precision-mode update: WASD nudges the aim point using the same
    /// acceleration curve as the camera WASD (input dir + accel timer →
    /// AnimationCurve → speed). Space confirms the throw, ESC cancels.
    /// RMB is ignored (user spec: "the rmb does not cancel the aim").
    /// The cursor-follow / dead-zone logic from UpdateAiming is OFF — the
    /// aim point only moves when the player presses WASD.
    /// </summary>
    void UpdatePrecisionAiming()
    {
        if (Mouse.current == null || _heldCap == null || Keyboard.current == null)
        {
            CancelAiming();
            return;
        }

        // ESC cancels the throw (PauseMenu yields ESC to us while in this state).
        if (Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            CancelAiming();
            return;
        }

        // Space confirms the throw.
        if (Keyboard.current.spaceKey.wasPressedThisFrame)
        {
            Fire();
            return;
        }

        // Keep the held cap at its hand slot (same as UpdateAiming).
        if (_hand != null)
        {
            Vector3 slotPos = _hand.GetCapSlotPosition(_heldCap);
            if (slotPos.sqrMagnitude > 0.0001f)
                _heldCapOriginalPos = slotPos;
        }
        _heldCap.UpdateHeldBasePosition(_heldCapOriginalPos);
        _heldCap.StepSimulation(Time.deltaTime, null);

        float capRadius = _heldCap != null ? _heldCap.Parameters.Radius : 0.5f;
        float dt = Time.deltaTime;

        // --- WASD aim nudge (camera-style acceleration) ---
        // Read raw input direction in screen-space (W=forward, S=back, A=left, D=right).
        Vector2 inputDir = Vector2.zero;
        if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed) inputDir.y += 1f;
        if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) inputDir.y -= 1f;
        if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) inputDir.x += 1f;
        if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) inputDir.x -= 1f;

        if (inputDir.sqrMagnitude > 0.001f)
        {
            inputDir.Normalize();
            _precisionAccelTimer += dt;
        }
        else
        {
            _precisionAccelTimer = 0f;
        }

        if (_precisionAccelTimer > 0f && PrecisionAimAccelerationCurve != null
            && PrecisionAimAccelerationCurve.length > 0)
        {
            // Project the screen-space input direction onto the world ground plane
            // using the PlayerCamera's basis. This makes W visually "forward" from
            // the player's viewpoint, regardless of camera yaw.
            Vector3 camRightFlat = PlayerCamera != null ? PlayerCamera.transform.right : Vector3.right;
            camRightFlat.y = 0f;
            camRightFlat = camRightFlat.sqrMagnitude > 0.001f ? camRightFlat.normalized : Vector3.right;
            Vector3 camFwdFlat = PlayerCamera != null ? PlayerCamera.transform.forward : Vector3.forward;
            camFwdFlat.y = 0f;
            camFwdFlat = camFwdFlat.sqrMagnitude > 0.001f ? camFwdFlat.normalized : Vector3.forward;

            Vector3 worldDir = (camRightFlat * inputDir.x + camFwdFlat * inputDir.y);
            if (worldDir.sqrMagnitude > 0.001f)
                worldDir.Normalize();

            float curveTime = Mathf.Min(_precisionAccelTimer,
                PrecisionAimAccelerationCurve.keys[PrecisionAimAccelerationCurve.length - 1].time);
            float speedMultiplier = PrecisionAimAccelerationCurve.Evaluate(curveTime);
            float currentSpeed = PrecisionAimSpeed * speedMultiplier;

            _aimPoint += new Vector2(worldDir.x, worldDir.z) * currentSpeed * dt;
        }

        // Clamp to zone boundaries (same logic as UpdateAiming — keeps the aim
        // point out of restricted zones and resets _isDirectAimAllowed).
        Vector3 aimPoint3D = CapMath.FromXZ(_aimPoint, 0f);
        if (OverlapsAimBlockingZone(aimPoint3D, capRadius))
        {
            if (_hasLastAllowedAimPoint)
                _aimPoint = ClampToZoneBoundary(_lastAllowedAimPoint, _aimPoint, capRadius);
        }
        aimPoint3D = CapMath.FromXZ(_aimPoint, 0f);
        if (!OverlapsAimBlockingZone(aimPoint3D, capRadius))
        {
            _lastAllowedAimPoint = _aimPoint;
            _hasLastAllowedAimPoint = true;
            _isDirectAimAllowed = true;
        }
        else
        {
            _isDirectAimAllowed = _hasLastAllowedAimPoint;
        }

        UpdateAimPreview();
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

    /// <summary>
    /// Returns the held cap's prediction-depth bonus from a PredictorCapEffect
    /// component, or 0 if the cap has no such component. Added to
    /// CapTuning.PredictionDepth during aim preview to give the player more
    /// flip trajectories when aiming with a predictor cap.
    /// </summary>
    int GetHeldCapPredictionBonus()
    {
        if (_heldCap == null) return 0;
        var predictor = _heldCap.GetComponent<PredictorCapEffect>();
        return predictor != null ? predictor.PredictionDepthBonus : 0;
    }

    /// <summary>
    /// True when a cap of <paramref name="radius"/> placed at
    /// <paramref name="groundPoint"/> would land OFF the field (i.e., would
    /// fall off the table). Mirrors the runtime test done by
    /// <c>CapFieldBoundary.Supports</c> via <c>CapTurnResolver.IsPointSupported</c>.
    /// Returns false when no resolver is available so the preview falls back to
    /// the safe “supported” assumption.
    ///
    /// NOTE: the radius argument is kept for API symmetry but the runtime
    /// fall-off test in <c>CapFieldBoundary.LateUpdate</c> calls
    /// <c>Supports(cap.GroundPosition, 0f)</c> — i.e., it only checks the
    /// CENTER. A cap whose center is on the field but whose body overhangs the
    /// edge is NOT removed. Callers that want to match the runtime should pass
    /// 0 for the radius.
    /// </summary>
    bool IsPointSupportedByField(Vector2 groundPoint, float radius)
    {
        if (_turnResolver == null) return true;
        return _turnResolver.IsPointSupported(groundPoint, radius);
    }

    /// <summary>
    /// True when this prediction's end position is outside the field, i.e. the
    /// predicted cap would fall off the table after the chain reaction.
    ///
    /// Matches the runtime removal test in <c>CapFieldBoundary.LateUpdate</c>,
    /// which is <c>!Supports(cap.GroundPosition, 0f)</c> — center only. A cap
    /// that straddles the edge (body overlaps, center on the field) does NOT
    /// fall off, so we pass 0 for the radius here, NOT pred.Cap.Parameters.Radius.
    /// </summary>
    bool PredictionWillFallOff(CapPrediction pred)
    {
        if (pred.Cap == null) return false;
        return !IsPointSupportedByField(pred.EndPosition, 0f);
    }

    void UpdateAimPreview()
    {
        if (!_isDirectAimAllowed)
        {
            _directHitSeeds.Clear();
            _predictionResults.Clear();
            _fullPredictions.Clear();
            _continuationPredictions.Clear();
            TrajectoryPreview?.Hide();
            return;
        }

        _throwForce = _heldCap != null ? _heldCap.Parameters.ThrowPower : 5f;
        float slammerRadius = _heldCap != null ? _heldCap.Parameters.Radius : 0.5f;

        CollectDirectHitPredictions(_aimPoint, _throwForce, slammerRadius, _directHitSeeds);

        // Predict the FULL chain — depth filtering happens below.
        _predictionResults.Clear();
        StackPeelOffPredictor.Predict(
            CapRegistry.AllCaps,
            _directHitSeeds,
            _tuning,
            _predictionResults);

        // Split predictions by depth:
        //   Depth < PredictionDepth  → full (trajectory + ghost)
        //   Depth == PredictionDepth → continuation (half trajectory, no ghost)
        //   Depth > PredictionDepth  → hidden
        //
        // When PredictionDepth = 0, ALL predictions at depth 0 become
        // continuations (dotted-line placeholders) — this shows the player
        // "something will be hit" without full detail.
        //
        // Continuations are filtered by toggle based on source:
        //   Direct → always shown (primary effect of the throw)
        //   Chain  → needs PredictContinuedChain
        //   Stack  → needs PredictContinuedStack
        //
        // Predictor cap bonus: if the held cap has a PredictorCapEffect,
        // add its PredictionDepthBonus to the effective depth.
        _fullPredictions.Clear();
        _continuationPredictions.Clear();
        _fullPredictionsFallOff.Clear();
        _continuationPredictionsFallOff.Clear();

        int depth = _tuning.PredictionDepth + GetHeldCapPredictionBonus();
        for (int i = 0; i < _predictionResults.Count; i++)
        {
            CapPrediction pred = _predictionResults[i];
            if (pred.Depth < depth)
            {
                _fullPredictions.Add(pred);
                _fullPredictionsFallOff.Add(PredictionWillFallOff(pred));
            }
            else if (pred.Depth == depth)
            {
                // N+1 continuation — check toggle based on source.
                // "Chain" toggle covers both Direct and Chain sources (any
                // non-stack prediction). "Stack" toggle covers peel-off caps.
                bool show = pred.Source == PredictionSource.Stack
                    ? _tuning.PredictContinuedStack
                    : _tuning.PredictContinuedChain;
                if (show)
                {
                    _continuationPredictions.Add(pred);
                    _continuationPredictionsFallOff.Add(PredictionWillFallOff(pred));
                }
            }
            // else: Depth > depth → hidden, drop.
        }

        // Collect effect radius zones from ANY ICapEffectRadius component
        // (bomb, defender, etc.). Works for both the held cap (throw) and
        // predicted caps (chain reaction).
        _bombZones.Clear();
        if (_heldCap != null)
        {
            var effects = _heldCap.GetComponents<ICapEffectRadius>();
            for (int e = 0; e < effects.Length; e++)
            {
                if (effects[e].ShouldTriggerOnSide(_heldCap.IsHeads))
                {
                    _bombZones.Add((CapMath.FromXZ(_aimPoint, 0f),
                                    effects[e].EffectRadius,
                                    effects[e].ZoneColor));
                }
            }
        }
        for (int i = 0; i < _fullPredictions.Count; i++)
        {
            CapPrediction pred = _fullPredictions[i];
            if (pred.Cap == null) continue;
            var effects = pred.Cap.GetComponents<ICapEffectRadius>();
            for (int e = 0; e < effects.Length; e++)
            {
                // For predicted caps: the effect triggers when the cap FLIPS
                // (chain reaction). The cap's side after flipping = WillLandHeads.
                if (effects[e].ShouldTriggerOnSide(pred.WillLandHeads))
                {
                    _bombZones.Add((CapMath.FromXZ(pred.EndPosition, 0f),
                                    effects[e].EffectRadius,
                                    effects[e].ZoneColor));
                }
            }
        }

        // The thrown cap itself will fall off when its aim point (the cap's
        // CENTER) is outside the field. The runtime removal test in
        // CapFieldBoundary.LateUpdate is Supports(cap.GroundPosition, 0f) —
        // center only, NOT the cap's radius. Passing 0 here matches that test.
        // When no resolver is wired up yet (sandbox scenes), assume supported —
        // same fall-through behavior as the runtime.
        bool thrownCapWillFallOff = _turnResolver != null
            && !IsPointSupportedByField(_aimPoint, 0f);

        TrajectoryPreview?.Show(
            ThrowOriginPos,
            _aimPoint,
            slammerRadius,
            _tuning,
            _fullPredictions,
            _continuationPredictions,
            _bombZones,
            thrownCapWillFallOff,
            _fullPredictionsFallOff,
            _continuationPredictionsFallOff);

        // Ghosts only for full predictions (not continuations).
        TrajectoryPreview?.ShowGhosts(_fullPredictions);
    }

    void CancelAiming()
    {
        TrajectoryPreview?.Hide();
        _hasGrab = false;

        if (_heldCap != null)
        {
            _heldCap.EndHeldToIdle();
            // Cap returns to its hand slot (HandAnchor-relative), NOT the
            // camera-following position. GetCapSlotPosition gives the current
            // slot position so the cap snaps back correctly even if the camera
            // moved during aiming.
            if (_hand != null)
            {
                Vector3 slotPos = _hand.GetCapSlotPosition(_heldCap);
                if (slotPos.sqrMagnitude > 0.0001f)
                    _heldCap.transform.position = slotPos;
            }
        }

        _heldCap = null;
        _isDirectAimAllowed = false;
        CurrentState = State.Idle;
    }

    public void SetTurnInputEnabled(bool enabled)
    {
        TurnInputEnabled = enabled;
        if (!enabled && (CurrentState == State.Aiming || CurrentState == State.PrecisionAiming))
            CancelAiming();
    }

    /// <summary>
    /// Abort the current turn — cancel aiming, hide preview, return to idle.
    /// Called by TurnController when the turn is forcibly ended.
    /// </summary>
    public void AbortTurn()
    {
        if (CurrentState == State.Aiming || CurrentState == State.PrecisionAiming)
            CancelAiming();

        TrajectoryPreview?.Hide();
        _directHitSeeds.Clear();
        _predictionResults.Clear();
        _fullPredictions.Clear();
        _continuationPredictions.Clear();
        _fullPredictionsFallOff.Clear();
        _continuationPredictionsFallOff.Clear();
        _isDirectAimAllowed = false;
        _hasLastAllowedAimPoint = false;
        CurrentState = State.Idle;
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

        // The throw starts from ThrowOriginPos (the viewport+depth projection
        // that the trajectory was rendered from). The cap is currently at its
        // hand slot (_heldCapOriginalPos), so we move it to the throw start.
        // This is the "transition to throw start" — the cap moves from its
        // hand position to the throw origin.
        Vector3 startPosition = ThrowOriginPos;
        Vector3 landingPosition = CapMath.FromXZ(_aimPoint, 0f);

        // Move the cap to the throw start position.
        cap.transform.position = startPosition;

        // Update the cap's rotation for the throw. The cap's rotation was set
        // by CapHand.LayoutHand to face the HandAnchor (which may differ from
        // the HandCamera). The user wants the cap's rotation relative to the
        // HAND CAMERA preserved — i.e., whatever rotation the cap had relative
        // to HandCamera before the throw, it should have the same rotation
        // relative to PlayerCamera at throw start.
        //
        // This is a frame-of-reference transfer:
        //   localRot = inverse(handCamera.rotation) * cap.rotation
        //   cap.rotation = PlayerCamera.rotation * localRot
        //
        // Source is always the HandCamera (NOT the HandAnchor) because the
        // user wants the hand camera's view preserved specifically. If no
        // HandCamera is assigned, fall back to PlayerCamera (no transfer).
        {
            Camera sourceCam = HandCamera != null ? HandCamera : PlayerCamera;
            Quaternion localRot = Quaternion.Inverse(sourceCam.transform.rotation) * cap.transform.rotation;
            cap.transform.rotation = PlayerCamera.transform.rotation * localRot;
        }

        // Clear grab state — the cap is no longer held.
        _hasGrab = false;

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

            if (!CapImpact.TryResolveHit(
                    slammerRadius,
                    CapImpactTarget.From(cap.Parameters),
                    landingPoint,
                    cap.GroundPosition,
                    throwForce,
                    _tuning,
                    out Vector2 direction,
                    out float inheritedForce,
                    out float travelDistance,
                    out bool stacks))
                continue;

            if (stacks) continue;

            results.Add(new CapPrediction(
                cap,
                0,
                cap.GroundPosition,
                direction,
                inheritedForce,
                travelDistance,
                willLandHeads: !cap.IsHeads,
                source: PredictionSource.Direct));
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
        _fullPredictions.Clear();
        _continuationPredictions.Clear();
        _fullPredictionsFallOff.Clear();
        _continuationPredictionsFallOff.Clear();
        _isDirectAimAllowed = false;
        _hasLastAllowedAimPoint = false;
        CurrentState = State.Idle;

        // Reset the hand: destroy all hand caps, restore deck from template, refill.
        if (_hand != null) _hand.ResetHand();
    }
}