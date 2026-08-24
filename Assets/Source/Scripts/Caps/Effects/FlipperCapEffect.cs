using UnityEngine;

/// <summary>
/// Which side the flipper cap must land on to trigger.
/// </summary>
public enum FlipperTriggerSide
{
    Face = 0,
    Back = 1,
    Either = 2
}

/// <summary>
/// Flips nearby caps IN PLACE when this cap finishes a flip OR when it lands
/// from a hand throw on the trigger side. Flipped caps toggle IsFace but do
/// NOT move — they stay at their current position. Stacks flip as a unit
/// (all caps flip, no peel-off, no scatter).
///
/// This is the "old bomb" behavior — flips caps without moving them.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Cap))]
public sealed class FlipperCapEffect : CapFlipEffect, ICapEffectRadius, ICapAbility
{
    [Header("Sticker")]
    [Tooltip("Icon sprite shown as a sticker above the cap.")]
    [SerializeField] private Sprite _stickerSprite;

    [Header("Level")]
    [Tooltip("Ability level (1-3). Higher levels = larger radius.")]
    [Range(1, 3)] [SerializeField] private int _level = 1;

    [Header("Level Parameters")]
    [Tooltip("Radius at level 1.")]
    [SerializeField] private float _radiusL1 = 3f;
    [Tooltip("Radius at level 2.")]
    [SerializeField] private float _radiusL2 = 4f;
    [Tooltip("Radius at level 3.")]
    [SerializeField] private float _radiusL3 = 5f;

    public Sprite StickerSprite => _stickerSprite;
    public int Level => _level;
    public string Description =>
        $"Когда приземляется лицом вверх, переворачивает все фишки в радиусе {Radius:F0}.";

    public float Radius => _level switch { 2 => _radiusL2, 3 => _radiusL3, _ => _radiusL1 };

    public float EffectRadius => Radius;

    [Header("Trigger")]
    [Tooltip("Which side the flipper must land on to trigger. " +
             "Face = triggers when it lands face-up. Back = triggers when back-up. " +
             "Either = triggers on any landing.")]
    public FlipperTriggerSide TriggerSide = FlipperTriggerSide.Face;

    public Color ZoneColor => new Color(0.8f, 0.3f, 1f, 0.35f); // purple

    [Header("Feedback")]
    public GameObject FlipVFX;
    public AudioClip FlipSound;
    [Range(0f, 2f)] public float FlipPitch = 1.2f;
    [Range(0f, 1f)] public float FlipVolume = 0.8f;
    public float FlipShakeAmount = 0.15f;
    public float FlipShakeDuration = 0.3f;

    [Header("Material Change")]
    [Tooltip("Material to switch to when the flipper triggers.")]
    public Material TriggerMaterial;
    [Tooltip("How long (in seconds) to stay in the trigger material.")]
    public float MaterialChangeDuration = 0.4f;

    public override void BuildCommands(
        in CapFlipEvent flipEvent,
        ICapEffectQuery query,
        ICapEffectCommandSink commands)
    {
        if (flipEvent.Source == null || Radius <= 0f) return;
        if (!ShouldTrigger(flipEvent.Source.IsFace)) return;

        // Per-turn trigger limit. Without this, two flippers with overlapping
        // radii ping-pong a cap forever: A flips B → B finishes flip → B's
        // flipper effect fires → flips A back → A finishes flip → A's flipper
        // effect fires → flips B back → repeat. The global MaximumChainLength
        // eventually stops it, but at 24 * ~0.4s animation that's ~10s of "looks
        // infinite" before the turn ends. This per-flipper counter is the real
        // circuit breaker — each flipper gets up to MaxFlipperTriggersPerTurn
        // triggers per throw, then goes silent for the rest of the turn.
        // Counter is reset on each new throw by CapTurnResolver.TryStartThrow.
        CapTuning tuning = CapTuning.Instance;
        if (tuning != null
            && !flipEvent.Source.TryConsumeFlipperTrigger(tuning.MaxFlipperTriggersPerTurn))
            return;

        commands.Add(new RadialFlipCommand(flipEvent.Source, flipEvent.Position, Radius));
    }

    /// <summary>
    /// Returns true if the flipper should trigger given the side it landed on.
    /// </summary>
    public bool ShouldTrigger(bool landedFace)
    {
        return TriggerSide switch
        {
            FlipperTriggerSide.Face => landedFace,
            FlipperTriggerSide.Back => !landedFace,
            FlipperTriggerSide.Either => true,
            _ => false,
        };
    }

    /// <summary>
    /// Describes this effect as a radial launch for the AI move search.
    /// Returns the radius and a nominal force (1, since flip doesn't use force).
    /// </summary>
    public override bool TryGetRadialLaunch(out float radius, out float force)
    {
        radius = Radius;
        force = 1f;
        return Radius > 0f;
    }

    void OnValidate()
    {
        _radiusL1 = Mathf.Max(0.01f, _radiusL1);
        _radiusL2 = Mathf.Max(0.01f, _radiusL2);
        _radiusL3 = Mathf.Max(0.01f, _radiusL3);
        _level = Mathf.Clamp(_level, 1, 3);
    }

    public override void PlayFeedback(Vector3 position, float force)
    {
        if (FlipVFX != null && VFXManager.Instance != null)
            VFXManager.Instance.Spawn(FlipVFX, position);

        if (FlipSound != null && AudioManager.Instance != null)
            AudioManager.Instance.Play3D(FlipSound, position, FlipPitch, FlipVolume);

        if (CameraShake.Instance != null)
            CameraShake.Instance.Shake(FlipShakeAmount, FlipShakeDuration);

        if (TriggerMaterial != null)
        {
            Cap cap = GetComponent<Cap>();
            if (cap != null)
            {
                cap.SetOverrideMaterial(TriggerMaterial, MaterialChangeDuration);
            }
        }
    }

    public bool ShouldTriggerOnSide(bool isFace) => ShouldTrigger(isFace);
}
