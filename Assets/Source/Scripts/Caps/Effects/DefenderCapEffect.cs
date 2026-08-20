using UnityEngine;

/// <summary>
/// Which side the defender cap must be showing for its zone to be active.
/// </summary>
public enum DefenderTriggerSide
{
    Heads = 0,
    Tails = 1,
    Either = 2,
}

/// <summary>
/// Creates a no-aim zone around this cap. While the cap is on the field and
/// showing the correct side, the opposite owner cannot aim a direct throw
/// into the zone — any part of the thrown cap touching the zone blocks the
/// throw. Chain reactions, pushes, and bomb explosions are NOT restricted
/// (same rule as ScoringZone.BlocksDirectAiming).
///
/// Auto-setup: the blocked thrower is derived automatically from the cap's
/// owner — a player-owned defender blocks the enemy (AI), an opponent-owned
/// defender blocks the player, a neutral defender blocks both.
///
/// The zone follows the cap's position (uses GroundPosition), so if the cap
/// is pushed or moved by a chain reaction, the zone moves with it. If the cap
/// is flipped to the wrong side (via a chain reaction), the zone deactivates
/// until it's flipped back.
///
/// Assign a child GameObject to <see cref="ZoneVisual"/> to show a visual
/// indicator (e.g., a transparent ring). The visual auto-scales to match
/// <see cref="ZoneRadius"/>, auto-toggles when the effect's active state
/// changes, and auto-swaps its material based on which side is restricted.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Cap))]
public sealed class DefenderCapEffect : MonoBehaviour, ICapEffectRadius, ICapAbility
{
    [Header("Sticker")]
    [Tooltip("Icon sprite shown as a sticker above the cap.")]
    [SerializeField] private Sprite _stickerSprite;

    [Header("Level")]
    [Tooltip("Ability level (1-3). Higher levels = larger radius.")]
    [Range(1, 3)] [SerializeField] private int _level = 1;

    [Header("Level Parameters")]
    [Tooltip("Zone radius at level 1.")]
    [SerializeField] private float _zoneRadiusL1 = 2f;
    [Tooltip("Zone radius at level 2.")]
    [SerializeField] private float _zoneRadiusL2 = 3f;
    [Tooltip("Zone radius at level 3.")]
    [SerializeField] private float _zoneRadiusL3 = 4f;

    public Sprite StickerSprite => _stickerSprite;
    public int Level => _level;
    public string Description =>
        $"Когда лежит лицом вверх, не дает противнику бросить фишку в зону радиусом {ZoneRadius:F0}.";

    public float ZoneRadius => _level switch { 2 => _zoneRadiusL2, 3 => _zoneRadiusL3, _ => _zoneRadiusL1 };

    [Tooltip("Which side the cap must be showing for the zone to be active. " +
             "Heads = zone active when cap is heads-up. Tails = active when tails-up. " +
             "Either = always active regardless of side.")]
    public DefenderTriggerSide TriggerSide = DefenderTriggerSide.Heads;

    [Header("Visual")]
    [Tooltip("Child GameObject that visualizes the zone (e.g., a transparent ring or cylinder). " +
             "Auto-scaled to match ZoneRadius, auto-toggled when the effect's active state changes, " +
             "and auto-swaps material based on the cap's owner.")]
    public GameObject ZoneVisual;

    [Tooltip("The visual's native diameter in world units. Used to compute scale: " +
             "localScale.x/z = ZoneRadius * 2 / ZoneVisualBaseSize. " +
             "1 = the visual is a 1x1 unit shape. 10 = the visual is a 10x10 unit shape (Unity's default Plane).")]
    [Min(0.01f)] public float ZoneVisualBaseSize = 1f;

    [Header("Materials (auto-selected by cap owner)")]
    [Tooltip("Material shown when the PLAYER is blocked from aiming " +
             "(defender owned by Opponent or Neutral).")]
    public Material PlayerRestrictedMaterial;

    [Tooltip("Material shown when the ENEMY/AI is blocked from aiming " +
             "(defender owned by Player).")]
    public Material EnemyRestrictedMaterial;

    private Cap _cap;
    private Renderer _visualRenderer;
    private Material _lastAppliedMaterial;

    void Awake()
    {
        _cap = GetComponent<Cap>();
        CacheVisualRenderer();
        UpdateVisualScale();
        UpdateVisualMaterial();
        UpdateVisualActive();
    }

    void OnValidate()
    {
        _zoneRadiusL1 = Mathf.Max(0.01f, _zoneRadiusL1);
        _zoneRadiusL2 = Mathf.Max(0.01f, _zoneRadiusL2);
        _zoneRadiusL3 = Mathf.Max(0.01f, _zoneRadiusL3);
        _level = Mathf.Clamp(_level, 1, 3);
        ZoneVisualBaseSize = Mathf.Max(0.01f, ZoneVisualBaseSize);
    }

    void Update()
    {
        // Re-evaluate every frame: the cap's side can change via chain reaction
        // flips, the cap can be thrown/landed, and the owner can change at runtime.
        UpdateVisualScale();
        UpdateVisualMaterial();
        UpdateVisualActive();
    }

    /// <summary>
    /// True if the defender's zone is currently active (cap on field, correct side).
    /// Checked by CapAimRules.IsBlockedByDefenderCap.
    ///
    /// "On field" means: the cap is in CapRegistry (hand caps are unregistered),
    /// AND its state is Idle (at rest on the field) or Pushed (being shoved by an
    /// impact). Held/Parked (in hand), Throwing/Flying (in flight) all return false.
    /// </summary>
    public bool IsZoneActive()
    {
        if (_cap == null) return false;

        // A cap that has left the game (fell off the field) should not have
        // an active defender zone, even though LeaveGame sets state to Idle.
        if (_cap.HasLeftGame) return false;

        // Hand caps are unregistered from CapRegistry — they're NOT "on field"
        // even though their state is Idle. This check excludes them.
        if (!CapRegistry.Contains(_cap)) return false;

        // Active only when the cap is on the field (Idle or Pushed).
        Cap.CapState state = _cap.CurrentState;
        if (state != Cap.CapState.Idle && state != Cap.CapState.Pushed) return false;

        // Side must match.
        return ShouldTrigger(_cap.IsHeads);
    }

    /// <summary>
    /// True if this defender blocks the given thrower's owner.
    /// Auto-derived from the cap's owner:
    ///   Player owner  → blocks Opponent (AI)
    ///   Opponent owner → blocks Player
    ///   Neutral owner → blocks both
    /// </summary>
    public bool BlocksThrower(CapOwner throwerOwner)
    {
        if (_cap == null) return false;

        return _cap.Owner switch
        {
            CapOwner.Player => throwerOwner == CapOwner.Opponent,
            CapOwner.Opponent => throwerOwner == CapOwner.Player,
            // Neutral blocks everyone.
            _ => true,
        };
    }

    /// <summary>
    /// Returns the cap's ground position (center of the zone). External callers
    /// use this to check if a landing point is inside the zone.
    /// </summary>
    public Vector2 ZoneCenter => _cap != null ? _cap.GroundPosition : Vector2.zero;

    /// <summary>ICapEffectRadius — same as ZoneRadius.</summary>
    public float EffectRadius => ZoneRadius;

    /// <summary>Cyan color for defender radius circles in the trajectory preview.</summary>
    public Color ZoneColor => new Color(0.2f, 0.6f, 1f, 0.3f);

    /// <summary>ICapEffectRadius — wraps the private ShouldTrigger for the trajectory preview.</summary>
    public bool ShouldTriggerOnSide(bool isHeads) => ShouldTrigger(isHeads);

    /// <summary>
    /// Returns true if the defender's trigger side matches the cap's current side.
    /// </summary>
    bool ShouldTrigger(bool landedHeads)
    {
        return TriggerSide switch
        {
            DefenderTriggerSide.Heads => landedHeads,
            DefenderTriggerSide.Tails => !landedHeads,
            DefenderTriggerSide.Either => true,
            _ => false,
        };
    }

    void CacheVisualRenderer()
    {
        if (ZoneVisual == null) return;
        _visualRenderer = ZoneVisual.GetComponent<Renderer>();
        if (_visualRenderer == null)
            _visualRenderer = ZoneVisual.GetComponentInChildren<Renderer>();
    }

    /// <summary>
    /// Returns the material to show based on which side is restricted.
    /// Player-owned defender → blocks enemy → EnemyRestrictedMaterial.
    /// Opponent-owned or Neutral → blocks player → PlayerRestrictedMaterial.
    /// (Neutral blocks both, but shows PlayerRestrictedMaterial per spec.)
    /// </summary>
    Material GetRestrictedMaterial()
    {
        if (_cap == null) return null;
        return _cap.Owner == CapOwner.Player ? EnemyRestrictedMaterial : PlayerRestrictedMaterial;
    }

    void UpdateVisualScale()
    {
        if (ZoneVisual == null) return;
        float scale = ZoneRadius * 2f / ZoneVisualBaseSize;
        Vector3 localScale = ZoneVisual.transform.localScale;
        localScale.x = scale;
        localScale.z = scale;
        ZoneVisual.transform.localScale = localScale;
    }

    void UpdateVisualMaterial()
    {
        if (_visualRenderer == null) return;

        Material target = GetRestrictedMaterial();
        if (target == null) return;

        // Only swap when the material actually changes (avoids per-frame
        // material assignments and handles runtime owner changes via SetOwner).
        if (_lastAppliedMaterial != target)
        {
            _visualRenderer.sharedMaterial = target;
            _lastAppliedMaterial = target;
        }
    }

    void UpdateVisualActive()
    {
        if (ZoneVisual == null) return;
        ZoneVisual.SetActive(IsZoneActive());
    }
}
