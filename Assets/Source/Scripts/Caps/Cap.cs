using UnityEngine;
using UnityEngine.Rendering;
using System.Linq;
using System.Collections.Generic;

public class Cap : MonoBehaviour
{
    public enum CapState { Idle, Held, Throwing, Flying, Pushed, Parked }

    [Header("Identity")]
    [SerializeField] private int _stableId;
    [SerializeField] private CapOwner _owner = CapOwner.Neutral;

    [Tooltip("Initial side of the cap when placed in the scene. " +
             "Checked = face-up, unchecked = back-up. " +
             "Only used for scene-placed caps (ignored for factory-created caps).")]
    [SerializeField] private bool _initialIsFace = true;

    public int StableId => _stableId;
    public CapOwner Owner => _owner;

    /// <summary>
    /// The RunManager.RunDeck entry ID this cap was created from. Set by
    /// CapFactory.CreateComposed at creation time. Used by RunManager to
    /// identify which deck entry a lost cap came from — replacing the old
    /// fragile GeneratedFaceSprite matching (which broke when multiple caps
    /// had the same face sprite, or when the face changed between scenes).
    ///
    /// 0 = not from the run deck (e.g., a scene-placed cap, or a cap created
    /// in sandbox mode without a deck entry).
    /// </summary>
    public int RunDeckEntryId { get; set; }

    /// <summary>
    /// The EFFECTIVE owner — if the cap is in a stack, the top cap's owner
    /// determines the effective owner for ALL caps in the stack (defender
    /// zone ownership, bomb target filtering, etc.). If standalone, returns
    /// the cap's own _owner.
    /// </summary>
    public CapOwner EffectiveOwner
    {
        get
        {
            Cap baseCap = FindStackBase();
            Cap topCap = baseCap.GetStackTop();
            return topCap._owner;
        }
    }

    [Header("Coin Parts (assign in prefab)")]
    [Tooltip("The renderer for the TOP face of the coin (face side).")]
    [SerializeField] private MeshRenderer _topRenderer;
    [Tooltip("The renderer for the BOTTOM face of the coin (back side).")]
    [SerializeField] private MeshRenderer _bottomRenderer;
    [Tooltip("The renderer for the RIM (edge) of the coin.")]
    [SerializeField] private MeshRenderer _rimRenderer;

    [Header("Team outline")]
    [SerializeField] private MeshRenderer _outlineRenderer;
    [SerializeField, Min(0f)] private float _outlineWidth = 0.035f;
    [Tooltip("Extra outline width when this cap is hovered by the cursor.")]
    [SerializeField, Min(0f)] private float _hoverOutlineBoost = 0.02f;
    [SerializeField] private Color _playerOutlineColor = new Color(0.05f, 0.9f, 0.85f, 1f);
    [SerializeField] private Color _opponentOutlineColor = new Color(1f, 0.2f, 0.05f, 1f);

    private bool _isHovered;

    [Header("Team rim materials")]
    [Tooltip("Material for the rim/side of the cap when owned by the Player.")]
    [SerializeField] private Material _playerRimMaterial;
    [Tooltip("Material for the rim/side of the cap when owned by the Opponent.")]
    [SerializeField] private Material _opponentRimMaterial;
    [Tooltip("Fallback material for the rim/side when the cap is Neutral.")]
    [SerializeField] private Material _neutralRimMaterial;

    [Header("No-loss marker (scene-placed player caps)")]
    [Tooltip("When set, scene-placed PLAYER caps (not from the run deck) have their " +
             "face (top) material replaced with this marker material. Identifies " +
             "these caps visually as 'starting layout' caps that don't count as " +
             "lost in the result screen when knocked off. Leave null to skip the " +
             "marker (no visual change).")]
    [SerializeField] private Material _noLossFaceMaterial;
    [Tooltip("When set, scene-placed PLAYER caps (not from the run deck) have their " +
             "back (bottom) material replaced with this marker material.")]
    [SerializeField] private Material _noLossBackMaterial;

    /// <summary>
    /// True if this cap is a scene-placed player cap that should NOT count as
    /// "lost" in the result screen when knocked off. Set on Awake for scene-placed
    /// player caps with RunDeckEntryId == 0. The enemy's kill count still
    /// increments (handled in TurnController.HandleCapLeftField, before
    /// RunManager.RecordCapLost is called).
    /// </summary>
    public bool IsNoLossMarker { get; private set; }

    [Header("Deck UI")]
    [Tooltip("Unique sprite representing THIS cap in the deck panel UI. Each cap prefab " +
             "should have its own DeckSprite assigned so the player can visually tell caps " +
             "apart in the deck. This is NOT a sticker — a cap can have multiple stickers " +
             "(one per ICapAbility), but only ONE DeckSprite that represents the cap itself. " +
             "If null, the deck panel falls back to the first sticker sprite or a default.")]
    [SerializeField] private Sprite _deckSprite;
    /// <summary>
    /// Unique sprite for this cap in the deck panel UI. If a CapVisualGenerator
    /// generated a face sprite, returns THAT (the actual face shown on the cap).
    /// Otherwise falls back to the inspector-assigned _deckSprite.
    /// </summary>
    public Sprite DeckSprite
    {
        get
        {
            var gen = GetComponent<CapVisualGenerator>();
            if (gen != null && gen.GeneratedFaceSprite != null)
                return gen.GeneratedFaceSprite;
            return _deckSprite;
        }
    }

    [Header("Cap parameters")]
    [SerializeField] private CapParameters _parameters = new CapParameters();
    public CapParameters Parameters => _parameters;

    public float GetContactFactor(float normalizedOffset) => _parameters.GetContactFactor(normalizedOffset);

    public Vector2 GroundPosition { get; private set; }
    public bool IsFace { get; internal set; } = true;
    public bool IsBusy => _state != CapState.Idle && _state != CapState.Parked;
    public bool IsThrowable => !_hasLeftGame && _state == CapState.Idle && _stackBase == null;
    public bool CanFlip => !_hasLeftGame && (_state == CapState.Idle || _state == CapState.Pushed) && _stackBase == null;
    public bool IsParked => _state == CapState.Parked;
    public CapState CurrentState => _state;
    public int ActivationDepthPlusOne => _activationDepth + 1;
    public int StackCount => _stackAbove.Count + 1;
    public bool WasPeelOff { get; set; }

    /// <summary>
    /// True if this cap was placed in the scene editor (not created via CapFactory.Create).
    /// Scene-placed caps are regenerated on board reset instead of destroyed.
    /// </summary>
    public bool IsScenePlaced => _isScenePlaced;
    internal bool _isScenePlaced;

    /// <summary>Initial position captured on Awake, used to restore on reset.</summary>
    private Vector3 _initialPosition;
    private Quaternion _initialRotation;

    /// <summary>True after the cap has left the playing field (fell off, handed to physics).</summary>
    public bool HasLeftGame => _hasLeftGame;

    /// <summary>Caps stacked on top of this one (bottom-to-top order). Empty if this cap is not a stack base.</summary>
    public IReadOnlyList<Cap> StackedAbove => _stackAbove;

    /// <summary>The cap this cap is stacked on top of, or null if this cap is a stack base / not stacked.</summary>
    public Cap StackBase => _stackBase;

    /// <summary>
    /// Walks up to the base of this cap's stack. Returns this if not part of a stack.
    /// Useful for aim prediction when you only have a stack member reference.
    /// </summary>
    public Cap FindStackBase()
    {
        Cap head = this;
        while (head._stackBase != null) head = head._stackBase;
        return head;
    }

    /// <summary>
    /// Returns the rotation that shows the requested side facing up.
    /// Identity = face up, 180° X rotation = back up.
    /// Used by the ghost-preview system to show which side the cap will land on.
    /// </summary>
    public Quaternion GetLandingRotation(bool willLandFace) =>
        willLandFace ? Quaternion.identity : Quaternion.Euler(180f, 0f, 0f);

    private bool _isImmutable;
    private bool _hasLeftGame;
    private CapFlipEffect[] _flipEffects;
    internal CapFlipEffect[] FlipEffects => _flipEffects;

    // Per-turn trigger counter for THIS cap's flipper effect (if any). Bounded
    // per turn so two overlapping flippers can't ping-pong a cap forever.
    // Incremented by FlipperCapEffect.BuildCommands via TryConsumeFlipperTrigger,
    // reset by CapTurnResolver.TryStartThrow for every cap in CapRegistry.
    private int _flipperTriggersThisTurn;
    public int FlipperTriggersThisTurn => _flipperTriggersThisTurn;

    private CapTuning _tuning;
    private List<MeshRenderer> _meshRenderers = new();
    private Dictionary<MeshRenderer, Material[]> _originalMaterials = new();
    private MaterialPropertyBlock _outlineProperties;

    // Material Override System
    private Material _overrideMaterial;
    private float _overrideMaterialTimer;

    private static readonly int OutlineColorId = Shader.PropertyToID("_OutlineColor");
    private static readonly int OutlineWidthId = Shader.PropertyToID("_OutlineWidth");

    private CapState _state = CapState.Idle;

    private Vector3 _throwStart;
    private Vector3 _throwEnd;
    private float _throwElapsed;
    private float _throwDuration;
    private float _throwArcHeight;
    private float _landingForce;

    private Vector3 _heldBasePos;
    private float _heldCurrentHeight;

    private Vector2 _flyStart;
    private Vector2 _flyDirection;
    private float _flyTotalDistance;
    private float _flyElapsed;
    private float _flyDuration;
    private int _activationDepth;

    /// <summary>
    /// Y-axis rotation preserved when the cap lands. Set to the cap's current
    /// Y rotation at the moment of landing. X and Z rotations are flattened
    /// (cap lies flat), but Y is kept so the cap doesn't snap to a default
    /// facing. Used by Idle and Pushed states in ApplyVisuals.
    /// </summary>
    private float _landingYaw;

    /// <summary>
    /// Full rotation captured when the cap starts flying (BeginLaunch).
    /// Used as the Slerp start point for the flip animation so the cap
    /// transitions smoothly from its current rotation (including Y) to the
    /// flipped rotation, without snapping or changing flip direction.
    /// </summary>
    private Quaternion _flyStartRot;

    private Vector2 _pushStart;
    private Vector2 _pushDirection;
    private float _pushRemaining;
    private float _pushElapsed;
    private float _pushTotalDuration;

    private List<Cap> _stackAbove = new();
    private Cap _stackBase;

    private bool _isPeeling;
    private float _peelTravelDistance;
    private float _peelDuration;
    private float _peelForce;
    private System.Action<Cap, Vector2, float> _pendingLandedCallback;

    public void Configure(int id, bool isFace, CapOwner owner = CapOwner.Neutral)
    {
        _stableId = id;
        _owner = owner;
        IsFace = isFace;
        // Reset RunDeckEntryId — caller (CapFactory.CreateComposed) sets it
        // AFTER Configure if the cap is from the run deck. Default 0 = not
        // from the run deck.
        RunDeckEntryId = 0;
        Vector3 pos = transform.position;
        GroundPosition = IsFinite(pos) ? CapMath.ToXZ(pos) : Vector2.zero;
        _state = CapState.Idle;
        _stackAbove.Clear();
        _stackBase = null;
        _isPeeling = false;
        _pendingLandedCallback = null;
        _hasLeftGame = false;
        WasPeelOff = false;
        _overrideMaterial = null;
        _overrideMaterialTimer = 0f;

        // Generate random face/back visuals if the prefab has a CapVisualGenerator.
        var visualGen = GetComponent<CapVisualGenerator>();
        if (visualGen != null)
            visualGen.GenerateVisuals();

        // Re-cache the (now-generated) materials so the override system
        // restores the generated ones, not the prefab defaults.
        CacheOriginalMaterials();

        ApplyVisuals();
        ApplyOutline();
        ApplyRimMaterial();
    }

    /// <summary>
    /// Regenerates a scene-placed cap for board reset: restores its initial
    /// position, rotation, and IsFace; resets state to Idle; re-registers in
    /// CapRegistry; and reapplies visuals. Called by GameManager.ResetBoard
    /// instead of destroying the cap.
    /// </summary>
    public void RegenerateForReset()
    {
        if (!IsScenePlaced) return;

        // If the cap fell off the field during play, a FallingCap component was
        // added, the Cap component was disabled, and the GameObject may have
        // been hidden (SetActive(false)). Undo all of this so the cap can
        // participate in the simulation again.
        FallingCap falling = GetComponent<FallingCap>();
        if (falling != null)
            Destroy(falling);
        enabled = true;
        if (!gameObject.activeSelf)
            gameObject.SetActive(true);

        // Re-enable colliders (FallingCap disables them when the cap leaves play).
        Collider[] colliders = GetComponentsInChildren<Collider>();
        for (int c = 0; c < colliders.Length; c++)
        {
            if (colliders[c] != null)
                colliders[c].enabled = true;
        }

        // Restore initial transform (the cap may have moved during play).
        transform.position = _initialPosition;
        transform.rotation = _initialRotation;
        IsFace = _initialIsFace;

        // Re-extract _landingYaw from the restored rotation so ApplyVisuals
        // preserves the designer-set yaw (same logic as Awake).
        Vector3 right = Vector3.ProjectOnPlane(transform.rotation * Vector3.right, Vector3.up);
        _landingYaw = right.sqrMagnitude > 0.001f
            ? Mathf.Atan2(right.x, right.z) * Mathf.Rad2Deg - 90f
            : 0f;

        // Reset state to Idle — clears flying/throwing/pushed/parked.
        _state = CapState.Idle;
        _stackAbove.Clear();
        _stackBase = null;
        _isPeeling = false;
        _pendingLandedCallback = null;
        _hasLeftGame = false;
        WasPeelOff = false;
        _isImmutable = false;
        _overrideMaterial = null;
        _overrideMaterialTimer = 0f;

        // Re-read GroundPosition from restored transform.
        Vector3 pos = transform.position;
        GroundPosition = IsFinite(pos) ? CapMath.ToXZ(pos) : Vector2.zero;

        // Assign a new stable ID (counter was reset by ResetIdCounter).
        _stableId = CapFactory.NextStableId();

        // Re-register.
        if (!CapRegistry.Contains(this))
            CapRegistry.Register(this);

        ApplyVisuals();
        ApplyOutline();
        ApplyRimMaterial();
    }

    public void SetImmutable(bool value) => _isImmutable = value;

    /// <summary>
    /// If this cap has not yet reached <paramref name="maxPerTurn"/> flipper
    /// triggers this turn, increment the counter and return true. Otherwise
    /// return false — the flipper effect should silently skip emitting its
    /// command. This is the per-flipper circuit breaker that stops two
    /// overlapping flippers from bouncing a cap back and forth forever.
    /// </summary>
    public bool TryConsumeFlipperTrigger(int maxPerTurn)
    {
        if (maxPerTurn <= 0) return true; // 0 or negative = unlimited
        if (_flipperTriggersThisTurn >= maxPerTurn) return false;
        _flipperTriggersThisTurn++;
        return true;
    }

    /// <summary>Reset the per-turn flipper trigger counter. Called by
    /// CapTurnResolver at the start of each throw.</summary>
    public void ResetFlipperTriggerCount() => _flipperTriggersThisTurn = 0;

    /// <summary>
    /// Marks this cap as factory-created (not scene-placed). Called by
    /// CapFactory.Create after Configure, to override the _isScenePlaced flag
    /// that Awake may have set (Awake runs during Instantiate, before Configure
    /// assigns a non-zero _stableId, so it can't distinguish scene-placed from
    /// factory-created at that point).
    /// </summary>
    public void MarkFactoryCreated() => _isScenePlaced = false;

    /// <summary>
    /// Re-caches the _flipEffects array. Called by CapFactory.CreateComposed
    /// AFTER dynamically adding ability components (Bomb/Flipper/Defender/Predictor).
    ///
    /// WHY: Cap.Awake runs during Object.Instantiate — BEFORE CreateComposed
    /// adds the ability components. So _flipEffects = GetComponents<CapFlipEffect>()
    /// in Awake returns an EMPTY array (the base prefab has no ability components).
    /// The CapEffectResolver uses FlipEffects to iterate effects — without this
    /// refresh, dynamically-added abilities are invisible to the resolver and
    /// never trigger.
    /// </summary>
    public void RefreshFlipEffects() => _flipEffects = GetComponents<CapFlipEffect>();

    public void SetOwner(CapOwner owner)
    {
        _owner = owner;
        ApplyOutline();
        ApplyRimMaterial();
    }

    public void BeginHeld(Vector3 basePos)
    {
        _heldBasePos = IsFinite(basePos) ? basePos : _tuning.SpawnPosition;
        _heldCurrentHeight = 0f;
        _state = CapState.Held;
        ApplyVisuals();
    }

    public void UpdateHeldBasePosition(Vector3 basePos)
    {
        if (_state == CapState.Held)
        {
            _heldBasePos = basePos;
        }
    }

    public void EndHeldToIdle()
    {
        _state = CapState.Idle;
        // Don't call ApplyVisuals() here — for a hand cap, the Idle branch
        // computes a flat rotation (Euler(0, _landingYaw, 0) * sideRot) which
        // is wrong for a hand cap that should face the camera. CapHand.LayoutHand
        // runs every frame in Update and sets the correct camera-facing rotation.
        // Calling ApplyVisuals here would snap the cap to the wrong orientation
        // for one frame before LayoutHand corrects it.
    }

    /// <summary>
    /// Moves the cap between standing on the board and waiting at its thrower's spawn point.
    /// A parked cap keeps whatever transform its thrower gives it, and chains and effects skip it.
    /// </summary>
    public void SetParked(bool value)
    {
        if (value)
        {
            if (_state != CapState.Idle) return;
            _state = CapState.Parked;
        }
        else
        {
            if (_state != CapState.Parked) return;
            _state = CapState.Idle;
        }
        ApplyVisuals();
    }

    /// <summary>Puts an aimed cap back into its thrower's hands. Returns to Parked rather than Idle.</summary>
    public void EndHeldToParked()
    {
        _state = CapState.Parked;
        ApplyVisuals();
    }

    public void BeginThrow(Vector3 start, Vector3 end, float force, float duration, float arcHeight)
    {
        if (!IsFinite(start) || !IsFinite(end) || float.IsNaN(force)) return;
        GroundPosition = CapMath.ToXZ(end);
        _throwStart = start;
        _throwEnd = end;
        _throwElapsed = 0f;
        _throwDuration = duration;
        _throwArcHeight = arcHeight;
        _landingForce = force;
        _state = CapState.Throwing;
        // Move the cap to the throw start immediately so there's no one-frame
        // gap where it's still at its old position (hand overlay) before
        // StepThrow runs next frame.
        transform.position = start;
        ApplyVisuals();
    }

    public bool BeginLaunch(int throwId, int depth, Vector2 direction, float force, float travelDistance, float duration, int ignoredSourceId)
    {
        if (_isImmutable) return false;
        if (!CanFlip) return false;
        if (float.IsNaN(direction.x) || float.IsNaN(direction.y)) return false;
        if (float.IsNaN(travelDistance) || float.IsNaN(force)) return false;
        if (!IsFinite(GroundPosition)) return false;

        _activationDepth = depth;
        _flyStartRot = transform.rotation; // capture full rotation before flip
        _flyStart = GroundPosition;
        _flyDirection = direction.sqrMagnitude > 0.000001f ? direction.normalized : Vector2.right;
        _flyTotalDistance = travelDistance;
        _flyElapsed = 0f;
        _flyDuration = duration;
        _landingForce = force;
        _isPeeling = _stackAbove.Count > 0;
        if (_isPeeling)
        {
            _peelTravelDistance = travelDistance;
            _peelDuration = duration;
            _peelForce = force;
        }
        _state = CapState.Flying;
        ApplyVisuals();
        for (int i = 0; i < _stackAbove.Count; i++)
        {
            // Capture each stacked cap's own start rotation so the flip
            // animation can be applied on top of IT, not the base's rotation.
            // Without this, stacked caps snap to the base's rotation at the
            // start of the flip animation.
            _stackAbove[i]._flyStartRot = _stackAbove[i].transform.rotation;
            _stackAbove[i].ApplyVisuals();
        }
        return true;
    }

    public void BeginPush(Vector2 direction, float distance, float duration)
    {
        if (_isImmutable) return;
        if (_hasLeftGame) return; // don't push caps that left the field
        if (_state != CapState.Idle) return;
        if (_stackBase != null) return;
        if (float.IsNaN(distance) || distance <= 0.0001f) return;
        if (!IsFinite(GroundPosition)) return;
        if (float.IsNaN(direction.x) || float.IsNaN(direction.y)) return;

        _pushStart = GroundPosition;
        _pushDirection = direction.sqrMagnitude > 0.000001f ? direction.normalized : Vector2.right;
        _pushRemaining = distance;
        _pushTotalDuration = Mathf.Max(0.01f, duration);
        _pushElapsed = 0f;
        _state = CapState.Pushed;
        ApplyVisuals();
    }

    /// <summary>
    /// Flips this cap IN PLACE — plays the flip animation (180° around the
    /// lateral axis) but does NOT move the cap. Toggles IsFace. For stacks,
    /// ALL caps in the stack flip together (no peel-off, no scatter).
    ///
    /// The cap ends up at the same GroundPosition, same yaw, but with IsFace
    /// toggled. Stacked caps also toggle IsFace.
    ///
    /// Used by the FlipperCapEffect (RadialFlipCommand).
    /// </summary>
    /// <param name="duration">How long the flip animation takes.</param>
    /// <param name="onFlipped">Callback when the flip completes.</param>
    public void BeginFlipInPlace(float duration, System.Action<Cap, Vector2, float> onFlipped = null)
    {
        if (_isImmutable) return;
        if (_hasLeftGame) return; // don't flip caps that left the field
        if (_state != CapState.Idle && _state != CapState.Pushed) return;
        if (_stackBase != null) return;
        if (!IsFinite(GroundPosition)) return;

        // Set up a flip with zero travel distance — the cap stays in place.
        _flyStartRot = transform.rotation;
        _flyStart = GroundPosition;
        _flyDirection = Vector2.right; // direction doesn't matter (travel = 0)
        _flyTotalDistance = 0f; // NO travel — flip in place
        _flyElapsed = 0f;
        _flyDuration = Mathf.Max(0.01f, duration);
        _landingForce = 0f;
        _isPeeling = false; // NO peel-off — the stack flips as a unit
        _state = CapState.Flying;

        // Capture stacked caps' start rotations for the flip animation.
        for (int i = 0; i < _stackAbove.Count; i++)
        {
            _stackAbove[i]._flyStartRot = _stackAbove[i].transform.rotation;
        }

        ApplyVisuals();
        for (int i = 0; i < _stackAbove.Count; i++)
        {
            _stackAbove[i].ApplyVisuals();
        }

        // Store the callback for when the flip completes.
        _pendingLandedCallback = onFlipped;
    }

    public void AddToStack(Cap incoming)
    {
        if (incoming == null || incoming == this || incoming._hasLeftGame) return;

        Cap head = this;
        while (head._stackBase != null)
            head = head._stackBase;

        // Flatten the incoming cap's rotation BEFORE adding it to the stack.
        // The cap may have a tilted rotation from the throw arc / frame-of-reference
        // transfer. When it joins the stack, ApplyVisuals will compute the flat
        // rotation (Euler(0, _landingYaw, 0) * sideRot). Setting it now prevents
        // a one-frame visual snap from the tilted rotation to the flat rotation.
        // _landingYaw was already extracted in StepThrow/StepFly before this runs.
        Quaternion incomingSideRot = incoming.IsFace
            ? Quaternion.identity
            : Quaternion.Euler(180f, 0f, 0f);
        incoming.transform.rotation = Quaternion.Euler(0f, incoming._landingYaw, 0f) * incomingSideRot;

        if (incoming._stackAbove.Count > 0)
        {
            for (int i = 0; i < incoming._stackAbove.Count; i++)
            {
                Cap c = incoming._stackAbove[i];
                c._stackBase = head;
                head._stackAbove.Add(c);
            }
            incoming._stackAbove.Clear();
        }
        incoming._stackBase = head;
        head._stackAbove.Add(incoming);
        // KEEP the incoming cap registered in CapRegistry — stacked caps are
        // still "on field" (counted by CountCapsOnField, visible to defender
        // checks, checked by CapFieldBoundary). The cap's CanFlip/IsThrowable
        // return false while stacked (because _stackBase != null), so it
        // won't be hit directly or pushed by nearby landings.
        if (!CapRegistry.Contains(incoming))
            CapRegistry.Register(incoming);

        incoming._state = CapState.Idle;
        // Position the cap on top of the base. ApplyVisuals will handle this too,
        // but setting it now ensures the cap is in the right place immediately.
        float yOff = _tuning != null ? _tuning.CapThickness : 0.1f;
        int myIndex = head._stackAbove.IndexOf(incoming) + 1;
        incoming.transform.position = head.transform.position + Vector3.up * (yOff * myIndex);
        head.ApplyVisuals();
    }

    /// <summary>
    /// Breaks this stack apart and returns every cap it consisted of, this one included.
    /// The caps keep their current position and are no longer driven by the stack base.
    /// </summary>
    public List<Cap> ReleaseStack()
    {
        var released = new List<Cap> { this };
        for (int i = 0; i < _stackAbove.Count; i++)
        {
            _stackAbove[i]._stackBase = null;
            released.Add(_stackAbove[i]);
        }
        _stackAbove.Clear();
        return released;
    }

    public Cap GetStackTop()
    {
        if (_stackAbove.Count == 0) return this;
        return _stackAbove[_stackAbove.Count - 1];
    }

    /// <summary>
    /// Takes the cap out of the game: it stops animating, reports itself as idle so nothing keeps
    /// waiting for it to settle, and refuses any further interaction. Used when the cap leaves the
    /// field and is handed over to the physics engine. A landing that was queued earlier can still
    /// resolve a few frames later, and it must not drag the cap back onto the table.
    /// </summary>
    public void LeaveGame()
    {
        _state = CapState.Idle;
        _isPeeling = false;
        _pendingLandedCallback = null;
        _isImmutable = false;
        _hasLeftGame = true;
    }

    public void StepSimulation(float deltaTime, System.Action<Cap, Vector2, float> onLanded, System.Action<Cap, Vector2, float> onFlipped = null)
    {
        if (!enabled) return;
        if (_stackBase != null) return;

        switch (_state)
        {
            case CapState.Held: StepHeld(deltaTime); break;
            case CapState.Throwing: StepThrow(deltaTime, onLanded); break;
            case CapState.Flying: StepFly(deltaTime, onLanded, onFlipped); break;
            case CapState.Pushed: StepPush(deltaTime); break;
        }
        ApplyVisuals();

        for (int i = 0; i < _stackAbove.Count; i++)
        {
            _stackAbove[i].ApplyVisuals();
        }

        // If a flip-in-place restructured the stack (old base became stacked,
        // old top became new base), the new base's ApplyVisuals hasn't been
        // called yet. Call it + its stacked caps so everyone is positioned.
        if (_stackBase != null && _stackBase._stackAbove.Count > 0)
        {
            _stackBase.ApplyVisuals();
            for (int i = 0; i < _stackBase._stackAbove.Count; i++)
            {
                _stackBase._stackAbove[i].ApplyVisuals();
            }
        }
    }

    void StepHeld(float dt)
    {
        float target = _tuning != null ? _tuning.GrabLiftHeight : 0.5f;
        float speed = _tuning != null ? _tuning.GrabLiftSpeed : 3f;
        _heldCurrentHeight = Mathf.MoveTowards(_heldCurrentHeight, target, speed * dt);
    }

    void StepThrow(float dt, System.Action<Cap, Vector2, float> onLanded)
    {
        _throwElapsed = Mathf.Min(_throwElapsed + dt, _throwDuration);
        float t = _throwDuration > 0f ? _throwElapsed / _throwDuration : 1f;

        Vector3 pos = Vector3.Lerp(_throwStart, _throwEnd, t);
        pos.y += _throwArcHeight * Mathf.Sin(t * Mathf.PI);
        if (!IsFinite(pos)) { _state = CapState.Idle; return; }
        transform.position = pos;

        if (_throwElapsed >= _throwDuration)
        {
            // Flatten: extract Y from the current rotation using the RIGHT vector
            // (not forward — forward is reversed when the cap is back-up due to
            // the 180° X flip, which also applies to hand-flipped caps).
            // Right is 90° clockwise from forward.
            Vector3 right = Vector3.ProjectOnPlane(transform.rotation * Vector3.right, Vector3.up);
            _landingYaw = right.sqrMagnitude > 0.001f ? Mathf.Atan2(right.x, right.z) * Mathf.Rad2Deg - 90f : 0f;
            _state = CapState.Idle;
            transform.position = _throwEnd;

            // Bug fix: if the landing position is off the field, don't fire
            // onLanded — the cap flew off the edge. CapFieldBoundary.LateUpdate
            // will handle the fall. Firing onLanded here would trigger
            // ResolveLanding → effects (bomb, flipper) on a cap that's
            // about to leave the game.
            if (!IsLandingOnField())
                return;

            onLanded?.Invoke(this, GroundPosition, _landingForce);
        }
    }

    void StepFly(float dt, System.Action<Cap, Vector2, float> onLanded, System.Action<Cap, Vector2, float> onFlipped)
    {
        _flyElapsed += dt;
        float t = _flyDuration > 0f ? Mathf.Clamp01(_flyElapsed / _flyDuration) : 1f;
        Vector2 next = _flyStart + _flyDirection * (_flyTotalDistance * t);
        if (!IsFinite(next))
        {
            _state = CapState.Idle;
            return;
        }
        GroundPosition = next;

        if (_flyElapsed >= _flyDuration)
        {
            GroundPosition = _flyStart + _flyDirection * _flyTotalDistance;
            if (!IsFinite(GroundPosition)) GroundPosition = _flyStart;

            // Bug fix: if the landing position is off the field, don't fire
            // onFlipped/onLanded — the cap flew off the edge. CapFieldBoundary
            // will handle the fall. Firing callbacks here would trigger effects
            // (bomb, flipper) and chain reactions on a cap that's leaving the game.
            bool landedOffField = !IsLandingOnField();

            // Toggle IsFace on every landing (including peel-off continuation
            // flights). Each peel-off cap flips once per iteration it survives:
            // iteration 1 → flipped once, iteration 2 → flipped twice (back to
            // original), etc. This produces the alternating pattern:
            // 2-stack [h,h] → [t,h], 3-stack [h,h,h] → [t,h,t], etc.
            IsFace = !IsFace;
            for (int i = 0; i < _stackAbove.Count; i++)
            {
                _stackAbove[i].IsFace = !_stackAbove[i].IsFace;
            }

            // Only fire onFlipped if the cap landed on the field. If it flew
            // off the edge, don't trigger flip effects (bomb, flipper).
            if (!landedOffField)
            {
                // Fire onFlipped for the BASE cap.
                onFlipped?.Invoke(this, GroundPosition, _landingForce);

                // Fire onFlipped for EVERY cap in the stack. Each stacked cap
                // has its own flipper/bomb effect that should trigger when it
                // lands the correct side up. The CapEffectResolver excludes
                // same-stack caps from the effect radius so a bomb in the stack
                // doesn't push other stacked caps.
                for (int i = 0; i < _stackAbove.Count; i++)
                {
                    onFlipped?.Invoke(_stackAbove[i], GroundPosition, _landingForce);
                }
            }

            // Flatten: extract Y from the current post-flip rotation using
            // the RIGHT vector (not forward — forward is reversed by the 180°
            // flip, right is not). Right is 90° clockwise from forward.
            Vector3 right = Vector3.ProjectOnPlane(transform.rotation * Vector3.right, Vector3.up);
            _landingYaw = right.sqrMagnitude > 0.001f ? Mathf.Atan2(right.x, right.z) * Mathf.Rad2Deg - 90f : 0f;

            // Extract _landingYaw for each stacked cap from THEIR OWN transform.rotation.
            // Stacked caps have their own _flyStartRot and may have a different yaw
            // than the base. Without this, they inherit the base's _landingYaw in
            // HandleStackPeelOff (line 493), causing a rotation snap when they
            // transition from the animated flip to the flat rest rotation.
            for (int i = 0; i < _stackAbove.Count; i++)
            {
                Cap stacked = _stackAbove[i];
                Vector3 stackedRight = Vector3.ProjectOnPlane(stacked.transform.rotation * Vector3.right, Vector3.up);
                stacked._landingYaw = stackedRight.sqrMagnitude > 0.001f
                    ? Mathf.Atan2(stackedRight.x, stackedRight.z) * Mathf.Rad2Deg - 90f
                    : 0f;
            }

            if (_isPeeling && _stackAbove.Count > 0)
            {
                _pendingLandedCallback = onLanded;
                HandleStackPeelOff();
                return;
            }

            // Flip-in-place: the whole stack flipped as a unit. The cap that was
            // on top is now on the bottom (and vice versa). Reverse the
            // _stackAbove list so ApplyVisuals positions them correctly.
            // This must happen BEFORE ApplyVisuals is called (which happens via
            // _state = CapState.Idle → next frame's LateUpdate or immediate
            // callers). The reversal is only for flip-in-place (travel=0), not
            // for launches (which go through HandleStackPeelOff above).
            // Flip-in-place: the whole stack flipped as a unit. A physical 180°
            // flip inverts the entire stack: old base → top, old top → base.
            // Build the full stack, reverse it, and reassign base/stacked roles.
            if (_flyTotalDistance == 0f && _stackAbove.Count > 0)
            {
                var fullStack = new List<Cap> { this };
                for (int i = 0; i < _stackAbove.Count; i++)
                    fullStack.Add(_stackAbove[i]);
                fullStack.Reverse();

                Cap newBase = fullStack[0];
                fullStack.RemoveAt(0);

                _stackAbove.Clear();

                if (newBase != this)
                {
                    newBase.GroundPosition = GroundPosition;
                    newBase._landingYaw = _landingYaw;
                    _stackBase = newBase;
                    newBase._stackAbove.Clear();
                    newBase._stackAbove.AddRange(fullStack);
                    for (int i = 0; i < newBase._stackAbove.Count; i++)
                        newBase._stackAbove[i]._stackBase = newBase;
                    newBase._stackBase = null;
                    newBase._state = CapState.Idle;
                    _state = CapState.Idle;
                }
            }

            _isPeeling = false;
            _state = CapState.Idle;

            // For flip-in-place (travel distance = 0), don't call onLanded —
            // the cap didn't move, so there are no chain reactions to resolve.
            // Also skip if the cap landed off the field.
            if (_flyTotalDistance > 0f && !landedOffField)
                onLanded?.Invoke(this, GroundPosition, _landingForce);
        }
    }

    void HandleStackPeelOff()
    {
        var fullStack = new List<Cap> { this };
        fullStack.AddRange(_stackAbove);

        fullStack.Reverse();

        Cap leftBehind = fullStack[0];
        fullStack.RemoveAt(0);

        leftBehind.GroundPosition = GroundPosition;
        leftBehind._stackAbove.Clear();
        leftBehind._stackBase = null;
        leftBehind._state = CapState.Idle;
        leftBehind._isPeeling = false;
        leftBehind._pendingLandedCallback = null;
        leftBehind.WasPeelOff = true;
        // leftBehind._landingYaw was already extracted from its own transform.rotation
        // in StepFly (before HandleStackPeelOff ran). Don't overwrite with the base's.
        if (!CapRegistry.AllCaps.Contains(leftBehind))
            CapRegistry.Register(leftBehind);
        leftBehind.ApplyVisuals();

        _pendingLandedCallback?.Invoke(leftBehind, GroundPosition, _peelForce);

        if (fullStack.Count == 0)
        {
            return;
        }

        Cap newHead = fullStack[0];
        fullStack.RemoveAt(0);

        newHead._stackAbove.Clear();
        for (int i = 0; i < fullStack.Count; i++)
        {
            fullStack[i]._stackBase = newHead;
            newHead._stackAbove.Add(fullStack[i]);
        }
        newHead._stackBase = null;

        if (!CapRegistry.AllCaps.Contains(newHead))
            CapRegistry.Register(newHead);

        newHead._isPeeling = newHead._stackAbove.Count > 0;
        newHead._peelTravelDistance = _peelTravelDistance;
        newHead._peelDuration = _peelDuration;
        newHead._peelForce = _peelForce;
        newHead._pendingLandedCallback = _pendingLandedCallback;
        newHead.GroundPosition = GroundPosition;
        newHead._flyStart = GroundPosition;
        newHead._flyDirection = _flyDirection;
        newHead._flyTotalDistance = _peelTravelDistance;
        newHead._flyElapsed = 0f;
        newHead._flyDuration = _peelDuration;
        newHead._landingForce = _peelForce;
        // Capture the cap's current rotation as the flip-animation start point.
        // Without this, the flip animation uses a stale/default _flyStartRot
        // and the cap's rotation doesn't animate correctly during the peel-off flight.
        newHead._flyStartRot = newHead.transform.rotation;
        newHead._state = CapState.Flying;
        newHead.ApplyVisuals();
        for (int i = 0; i < newHead._stackAbove.Count; i++)
        {
            // Capture each stacked cap's own start rotation for the flip animation.
            newHead._stackAbove[i]._flyStartRot = newHead._stackAbove[i].transform.rotation;
            newHead._stackAbove[i].ApplyVisuals();
        }
    }

    void StepPush(float dt)
    {
        _pushElapsed += dt;
        float t = Mathf.Clamp01(_pushElapsed / _pushTotalDuration);
        float eased = 1f - (1f - t) * (1f - t);
        float travelled = _pushRemaining * eased;
        Vector2 next = _pushStart + _pushDirection * travelled;
        if (!IsFinite(next))
        {
            _state = CapState.Idle;
            return;
        }

        // Chain-push collision detection: check if the cap's path from its
        // current position to `next` crosses any other cap. If so, push that
        // cap in the same direction (with remaining force proportional to
        // remaining travel distance).
        Vector2 previousPos = GroundPosition;
        CheckChainPushCollisions(previousPos, next);

        GroundPosition = next;
        if (t >= 1f) _state = CapState.Idle;
    }

    /// <summary>
    /// Checks if this pushed cap's path crosses any other cap. If so, pushes
    /// that cap in the same direction (chain-push). The pushed cap receives a
    /// push distance proportional to the remaining travel of this cap.
    /// </summary>
    void CheckChainPushCollisions(Vector2 from, Vector2 to)
    {
        float myRadius = _parameters != null ? _parameters.Radius : 0.5f;
        Vector2 delta = to - from;
        float moveDistance = delta.magnitude;
        if (moveDistance <= 0.0001f) return;
        Vector2 moveDir = delta / moveDistance;

        // The remaining push distance this cap would travel after the collision.
        float remainingPush = _pushRemaining * (1f - Mathf.Clamp01(_pushElapsed / _pushTotalDuration));

        IReadOnlyList<Cap> allCaps = CapRegistry.AllCaps;
        for (int i = 0; i < allCaps.Count; i++)
        {
            Cap other = allCaps[i];
            if (other == null || other == this) continue;
            if (other._stackBase != null) continue; // skip stacked caps
            if (!other.CanFlip) continue; // not pushable
            if (other._state != CapState.Idle) continue; // already moving

            float otherRadius = other._parameters != null ? other._parameters.Radius : 0.5f;
            float combinedRadius = myRadius + otherRadius;

            // Check if the segment from→to passes within combinedRadius of the other cap.
            Vector2 toOther = other.GroundPosition - from;
            float projection = Vector2.Dot(toOther, moveDir);

            // Skip if the other cap is behind the start of the segment.
            if (projection < 0f) continue;
            // Skip if the other cap is beyond the end of the segment.
            if (projection > moveDistance + combinedRadius) continue;

            // Closest point on the segment to the other cap.
            float clampedProjection = Mathf.Clamp(projection, 0f, moveDistance);
            Vector2 closestPoint = from + moveDir * clampedProjection;
            float distanceToOther = Vector2.Distance(other.GroundPosition, closestPoint);

            if (distanceToOther < combinedRadius)
            {
                // Collision — push the other cap. Use the direction from this cap
                // to the other cap (radial push) so caps don't all pile up in one line.
                Vector2 pushDir = other.GroundPosition - GroundPosition;
                if (pushDir.sqrMagnitude > 0.0001f)
                    pushDir.Normalize();
                else
                    pushDir = _pushDirection;

                // Push distance = remaining push of this cap (transferred force).
                float pushDistance = Mathf.Max(0.1f, remainingPush);
                other.BeginPush(pushDir, pushDistance, _pushTotalDuration - _pushElapsed);
            }
        }
    }

    /// <summary>
    /// Flip the cap in hand — toggle which side faces up (180° X) AND rotate
    /// 180° on Y so the texture on the new face is right-side-up. Called when
    /// the player presses F or RMB while hovering a hand cap.
    /// </summary>
    public void FlipInHand()
    {
        IsFace = !IsFace;
        _handFlipYaw = (_handFlipYaw + 180f) % 360f;
    }

    /// <summary>
    /// Clear the hand flip state. Called by CapHand.ReleaseCapForThrow so the
    /// hand flip Y rotation doesn't leak into the throw/flight/landing rotation.
    /// </summary>
    public void ClearHandFlip()
    {
        _handFlipYaw = 0f;
    }

    /// <summary>
    /// Additional Y rotation applied to caps in hand (for the F/RMB flip).
    /// 0 = default facing, 180 = rotated so texture faces the other way.
    /// CapHand.LayoutHand reads this when setting the hand rotation.
    /// </summary>
    public float HandFlipYaw => _handFlipYaw;
    private float _handFlipYaw;

    /// <summary>
    /// Sets a temporary material to be displayed on the cap.
    /// Pass null to clear it immediately.
    /// </summary>
    public void SetOverrideMaterial(Material mat, float duration)
    {
        // IMPORTANT: cache the cap's CURRENT materials BEFORE applying the override.
        // This ensures the override system restores EXACTLY what the cap had right
        // before the VFX triggered — not whatever was cached at Configure time,
        // not what was cached at SetGeneratedSprites time, but the live state at
        // the moment of the override. This is the robust fix for "bomb/flipper
        // reverts to a different sprite" — the cache is always fresh.
        //
        // Only cache if we're not ALREADY in an override (otherwise we'd cache the
        // override material itself, which is wrong).
        if (_overrideMaterial == null && mat != null)
            CacheOriginalMaterials();

        _overrideMaterial = mat;
        _overrideMaterialTimer = duration;
    }

    void ApplyVisuals()
    {
        if (_tuning == null) _tuning = CapTuning.Instance;

        // A parked cap is positioned by its thrower — don't touch its transform.
        if (_state == CapState.Parked && _stackBase == null)
            return;

        // Base rotation: identity if face-up, 180° X-flip if back-up.
        // This is applied to ALL non-flying states so the 3D model shows the
        // correct face. Flying state overrides this with its own flip animation.
        // The landing Y rotation (_landingYaw) is composed on top so the cap
        // keeps facing the direction it landed in, instead of snapping to a
        // default Y orientation.
        Quaternion sideRot = IsFace ? Quaternion.identity : Quaternion.Euler(180f, 0f, 0f);
        Quaternion yawRot = Quaternion.Euler(0f, _landingYaw, 0f);
        Quaternion flatRot = yawRot * sideRot; // flat + correct side + landing Y

        Vector3 pos;
        Quaternion rot = flatRot;

        if (_stackBase != null)
        {
            float yOff = _tuning != null ? _tuning.CapThickness : 0.1f;
            int myIndex = _stackBase._stackAbove.IndexOf(this) + 1;

            if (_stackBase._state == CapState.Flying)
            {
                // During the flip, the whole stack rotates as a rigid body.
                // The position offset must rotate WITH the base — use the base's
                // rotation applied to the up vector, so stacked caps ORBIT
                // around the base instead of staying at a fixed world-space height.
                Vector3 rotatedUp = _stackBase.transform.rotation * Vector3.up;
                pos = _stackBase.transform.position + rotatedUp * (yOff * myIndex);
                // Inherit the base's exact rotation — rigid body.
                rot = _stackBase.transform.rotation;
            }
            else
            {
                // At rest: flat rotation with own yaw + own side.
                pos = _stackBase.transform.position + Vector3.up * (yOff * myIndex);
                rot = Quaternion.Euler(0f, _landingYaw, 0f) * sideRot;
            }
        }
        else
        {
            switch (_state)
            {
                case CapState.Held:
                    pos = _heldBasePos + Vector3.up * _heldCurrentHeight;
                    // Held caps: don't override rotation. CapHand.LayoutHand sets
                    // the rotation to face the camera, and we preserve it here.
                    // Only the side (IsFace) is encoded — LayoutHand already
                    // applies sideRot in its rotation.
                    rot = transform.rotation;
                    break;

                case CapState.Throwing:
                    pos = transform.position;
                    // No spin on throw — cap keeps its current rotation.
                    rot = transform.rotation;
                    break;

                case CapState.Flying:
                    float flyProgress = _flyDuration > 0f ? Mathf.Clamp01(_flyElapsed / _flyDuration) : 1f;
                    float hop = Mathf.Sin(flyProgress * Mathf.PI) * _tuning.CapFlipApexHeight;
                    pos = CapMath.FromXZ(GroundPosition, hop);
                    Vector3 motion3D = new Vector3(_flyDirection.x, 0f, _flyDirection.y);
                    Vector3 rotAxis = Vector3.Cross(Vector3.up, motion3D);
                    if (!IsFinite(rotAxis) || rotAxis.sqrMagnitude < 0.0001f) rotAxis = Vector3.right;
                    else rotAxis = rotAxis.normalized;
                    // Flip animation: 180° around the world-space lateral axis,
                    // applied ON TOP of the start rotation. World-space (left multiply)
                    // keeps the flip axis correct regardless of the cap's Y rotation.
                    rot = Quaternion.AngleAxis(flyProgress * 180f, rotAxis) * _flyStartRot;
                    break;

                case CapState.Pushed:
                    pos = CapMath.FromXZ(GroundPosition, 0f);
                    // Pushed caps keep their landing Y rotation.
                    rot = flatRot;
                    break;

                default:
                    pos = CapMath.FromXZ(GroundPosition, 0f);
                    // Idle caps keep their landing Y rotation.
                    rot = flatRot;
                    break;
            }
        }

        if (!IsFinite(pos)) return;
        transform.position = pos;
        if (IsFinite(rot)) transform.rotation = rot;

        // Material handling: override ALL material slots on ALL child renderers
        // (e.g. bomb explosion), or restore the original materials when the
        // override expires. The cap is made of 3 separate meshes (top, bottom,
        // rim), each with its own MeshRenderer and material.
        if (_overrideMaterialTimer > 0 && _overrideMaterial != null)
        {
            // Override: replace ALL materials on ALL renderers with the override material.
            for (int r = 0; r < _meshRenderers.Count; r++)
            {
                MeshRenderer mr = _meshRenderers[r];
                if (mr == null) continue;

                Material[] current = mr.sharedMaterials;
                bool needsUpdate = current == null
                    || current.Length == 0
                    || current[0] != _overrideMaterial;
                if (needsUpdate)
                {
                    Material[] allOverride = new Material[current != null ? current.Length : 1];
                    for (int i = 0; i < allOverride.Length; i++) allOverride[i] = _overrideMaterial;
                    mr.sharedMaterials = allOverride;
                }
            }
            _overrideMaterialTimer -= Time.deltaTime;
        }
        else if (_overrideMaterial != null)
        {
            // Timer just expired — restore original materials on all renderers.
            for (int r = 0; r < _meshRenderers.Count; r++)
            {
                MeshRenderer mr = _meshRenderers[r];
                if (mr == null) continue;
                if (_originalMaterials.TryGetValue(mr, out Material[] originals))
                    mr.sharedMaterials = originals;
            }
            _overrideMaterial = null;
        }
    }

    static bool IsFinite(Vector3 v) =>
        !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) &&
        !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);

    static bool IsFinite(Vector2 v) =>
        !float.IsNaN(v.x) && !float.IsNaN(v.y) &&
        !float.IsInfinity(v.x) && !float.IsInfinity(v.y);

    static bool IsFinite(Quaternion q) =>
        !float.IsNaN(q.x) && !float.IsNaN(q.y) && !float.IsNaN(q.z) && !float.IsNaN(q.w) &&
        !float.IsInfinity(q.x) && !float.IsInfinity(q.y) && !float.IsInfinity(q.z) && !float.IsInfinity(q.w);

    /// <summary>
    /// Checks if the cap's current GroundPosition is on the field. Used by
    /// StepThrow and StepFly to determine whether to fire onLanded/onFlipped —
    /// if the cap landed off the field, its effects should NOT trigger.
    /// </summary>
    bool IsLandingOnField()
    {
        // Cache the field boundary on first use.
        if (_fieldBoundary == null)
            _fieldBoundary = FindFirstObjectByType<CapFieldBoundary>();
        if (_fieldBoundary == null)
            return true; // no boundary → assume on field
        return _fieldBoundary.Supports(GroundPosition, _parameters != null ? _parameters.Radius : 0.5f);
    }

    private static CapFieldBoundary _fieldBoundary;

    void ApplyOutline()
    {
        if (_outlineRenderer == null) return;

        bool hasOutline = _owner != CapOwner.Neutral
            && _outlineWidth > 0f
            && _outlineRenderer.sharedMaterial != null;

        _outlineRenderer.enabled = hasOutline;
        if (!hasOutline) return;

        float width = _isHovered ? _outlineWidth + _hoverOutlineBoost : _outlineWidth;

        _outlineProperties ??= new MaterialPropertyBlock();
        _outlineRenderer.GetPropertyBlock(_outlineProperties);
        _outlineProperties.SetColor(
            OutlineColorId,
            _owner == CapOwner.Player ? _playerOutlineColor : _opponentOutlineColor);
        _outlineProperties.SetFloat(OutlineWidthId, width);
        _outlineRenderer.SetPropertyBlock(_outlineProperties);
    }

    /// <summary>
    /// Sets the hover state for outline width boost. Called by StickerManager
    /// when the cursor enters/leaves the cap.
    /// </summary>
    public void SetHovered(bool hovered)
    {
        if (_isHovered == hovered) return;
        _isHovered = hovered;
        ApplyOutline();
    }

    /// <summary>
    /// Sets the rim renderer's material based on the cap's owner.
    /// Player → _playerRimMaterial, Opponent → _opponentRimMaterial,
    /// Neutral → _neutralRimMaterial (fallback — unlike the outline which
    /// turns off for neutral). If the specific material is null, falls back
    /// to the neutral material. If that's also null, the rim keeps whatever
    /// material it had originally.
    /// </summary>
    void ApplyRimMaterial()
    {
        if (_rimRenderer == null) return;

        Material target = _owner switch
        {
            CapOwner.Player => _playerRimMaterial ?? _neutralRimMaterial,
            CapOwner.Opponent => _opponentRimMaterial ?? _neutralRimMaterial,
            _ => _neutralRimMaterial,
        };

        if (target != null && _rimRenderer.sharedMaterial != target)
            _rimRenderer.sharedMaterial = target;
    }

    void Awake()
    {
        // Find the OutlineRenderer if not assigned.
        if (_outlineRenderer == null)
        {
            Transform outlineTransform = transform.Find("OutlineRenderer");
            if (outlineTransform != null)
                _outlineRenderer = outlineTransform.GetComponent<MeshRenderer>();
        }

        // Register the assigned coin-part renderers (top, bottom, rim).
        // Any null fields are silently skipped — e.g. a cap with no rim, or
        // a 2-mesh cap. Cache original materials for each so we can restore
        // them after a material override (e.g. bomb explosion) expires.
        TryRegisterRenderer(_topRenderer);
        TryRegisterRenderer(_bottomRenderer);
        TryRegisterRenderer(_rimRenderer);

        _flipEffects = GetComponents<CapFlipEffect>();

        if (_outlineRenderer != null)
        {
            _outlineRenderer.shadowCastingMode = ShadowCastingMode.Off;
            _outlineRenderer.receiveShadows = false;
            _outlineRenderer.lightProbeUsage = LightProbeUsage.Off;
            _outlineRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        _tuning = CapTuning.Instance;

        var rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = true;
            rb.useGravity = false;
        }

        // Auto-register scene-placed caps: if _stableId is 0 (the default for a
        // cap placed in the scene editor, not created via CapFactory.Create),
        // initialize it now and register in CapRegistry. This lets designers
        // place cap prefabs in the scene with Owner/IsFace set in the
        // inspector — no extra component needed.
        if (_stableId == 0)
        {
            // Capture initial transform BEFORE Configure changes anything.
            _initialPosition = transform.position;
            _initialRotation = transform.rotation;

            // Set IsFace from the serialized _initialIsFace field (inspector).
            IsFace = _initialIsFace;

            // Extract _landingYaw from the cap's initial transform rotation so
            // the designer-set yaw is preserved. Without this, Configure →
            // ApplyVisuals computes flatRot = Euler(0, _landingYaw=0, 0) * sideRot,
            // resetting the yaw to 0.
            // Use the RIGHT vector (not forward — forward is reversed by the 180°
            // X flip when back-up, right is not). Right is 90° clockwise from forward.
            Vector3 right = Vector3.ProjectOnPlane(transform.rotation * Vector3.right, Vector3.up);
            _landingYaw = right.sqrMagnitude > 0.001f
                ? Mathf.Atan2(right.x, right.z) * Mathf.Rad2Deg - 90f
                : 0f;

            Vector3 pos = transform.position;
            GroundPosition = IsFinite(pos) ? CapMath.ToXZ(pos) : Vector2.zero;
            Configure(CapFactory.NextStableId(), IsFace, _owner);
            // Configure doesn't register (it's called by CapFactory.Create which
            // does the registration). Register here for scene-placed caps.
            if (!CapRegistry.Contains(this))
                CapRegistry.Register(this);
            _isScenePlaced = true;

            // Scene-placed PLAYER caps that aren't from the run deck are "starting
            // layout" caps — they don't count as "lost" in the result screen when
            // knocked off (RunManager.RecordCapLost skips them). Mark them visually
            // with the no-loss face/back materials so the player can tell them
            // apart from run-deck caps. RunDeckEntryId is 0 for scene-placed caps
            // (CapFactory.CreateComposed stamps a non-zero ID for run-deck caps).
            // Only PLAYER caps are marked — scene-placed enemy caps still count
            // as "gained" when the player knocks them off.
            if (_owner == CapOwner.Player)
            {
                IsNoLossMarker = true;
                ApplyNoLossMarkerMaterials();
            }
        }

        ApplyOutline();
        ApplyRimMaterial();
    }

    /// <summary>
    /// Replaces the cap's face (top) and back (bottom) materials with the
    /// no-loss marker materials (_noLossFaceMaterial / _noLossBackMaterial).
    /// Called on Awake for scene-placed player caps. If a marker material is
    /// null, that face keeps its original material.
    /// </summary>
    void ApplyNoLossMarkerMaterials()
    {
        if (_topRenderer != null && _noLossFaceMaterial != null)
            _topRenderer.sharedMaterial = _noLossFaceMaterial;
        if (_bottomRenderer != null && _noLossBackMaterial != null)
            _bottomRenderer.sharedMaterial = _noLossBackMaterial;
        // Re-cache the original materials so the material-override system
        // (bomb/flipper VFX) restores the MARKED materials after the override
        // expires — not the pre-marker originals.
        CacheOriginalMaterials();
    }

    /// <summary>
    /// Register a coin-part renderer in the tracked list and cache its original
    /// materials. Silently skips null renderers (e.g. a cap with no rim).
    /// </summary>
    void TryRegisterRenderer(MeshRenderer mr)
    {
        if (mr == null) return;
        if (_meshRenderers.Contains(mr)) return;
        _meshRenderers.Add(mr);
        if (mr.sharedMaterials != null)
            _originalMaterials[mr] = (Material[])mr.sharedMaterials.Clone();
    }

    /// <summary>
    /// Re-caches the current sharedMaterials on all registered renderers.
    /// Called after CapVisualGenerator.GenerateVisuals() replaces the materials
    /// with randomly-generated clones. Without this, the material-override system
    /// (e.g. bomb explosion) would restore the OLD prefab-default materials
    /// instead of the generated ones.
    ///
    /// Also called after CapVisualGenerator.SetGeneratedSprites() — which replaces
    /// the materials with STORED sprites (e.g., from a run deck entry). Without
    /// this re-cache, the override system would restore the materials from BEFORE
    /// SetGeneratedSprites ran (e.g., the freshly-generated random face from
    /// CapFactory.Create → Configure → GenerateVisuals), causing the cap to
    /// "revert to a different sprite" after the bomb/flipper VFX expires.
    /// </summary>
    public void CacheOriginalMaterials()
    {
        for (int i = 0; i < _meshRenderers.Count; i++)
        {
            MeshRenderer mr = _meshRenderers[i];
            if (mr == null) continue;
            if (mr.sharedMaterials != null)
                _originalMaterials[mr] = (Material[])mr.sharedMaterials.Clone();
        }
    }

    void OnValidate()
    {
        ApplyOutline();
        ApplyRimMaterial();
    }

    void OnDestroy()
    {
        CapRegistry.Unregister(this);
    }
}

public static class CapRegistry
{
    private static readonly System.Collections.Generic.List<Cap> _allCaps = new();

    public static System.Collections.Generic.IReadOnlyList<Cap> AllCaps => _allCaps;

    public static void Register(Cap cap)
    {
        if (cap != null && !_allCaps.Contains(cap))
            _allCaps.Add(cap);
    }

    public static void Unregister(Cap cap) => _allCaps.Remove(cap);
    public static bool Contains(Cap cap) => _allCaps.Contains(cap);
    public static Cap[] Snapshot() => _allCaps.ToArray();
    public static void Clear() => _allCaps.Clear();

    internal static void RemoveAt(int index) => _allCaps.RemoveAt(index);
}