using UnityEngine;

/// <summary>
/// Which side the flipper cap must land on to trigger.
/// </summary>
public enum FlipperTriggerSide
{
    Heads = 0,
    Tails = 1,
    Either = 2
}

/// <summary>
/// Flips nearby caps IN PLACE when this cap finishes a flip OR when it lands
/// from a hand throw on the trigger side. Flipped caps toggle IsHeads but do
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

    public Sprite StickerSprite => _stickerSprite;
    public string Description =>
        $"Когда приземляется лицом вверх, переворачивает все фишки в радиусе {_radius:F0}.";

    [Header("Trigger")]
    [Tooltip("Which side the flipper must land on to trigger. " +
             "Heads = triggers when it lands heads-up. Tails = triggers when tails-up. " +
             "Either = triggers on any landing.")]
    public FlipperTriggerSide TriggerSide = FlipperTriggerSide.Heads;

    [SerializeField, Min(0.01f)]
    [Tooltip("Radius on the XZ plane, measured between cap centres. " +
             "Caps inside this radius are flipped in place.")]
    private float _radius = 3f;

    public float Radius => _radius;

    public float EffectRadius => _radius;

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
        if (flipEvent.Source == null || _radius <= 0f) return;
        if (!ShouldTrigger(flipEvent.Source.IsHeads)) return;
        // RadialFlipCommand — flips caps in place (no movement, no peel-off).
        commands.Add(new RadialFlipCommand(flipEvent.Source, flipEvent.Position, _radius));
    }

    /// <summary>
    /// Returns true if the flipper should trigger given the side it landed on.
    /// </summary>
    public bool ShouldTrigger(bool landedHeads)
    {
        return TriggerSide switch
        {
            FlipperTriggerSide.Heads => landedHeads,
            FlipperTriggerSide.Tails => !landedHeads,
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
        radius = _radius;
        force = 1f;
        return _radius > 0f;
    }

    void OnValidate()
    {
        _radius = Mathf.Max(0.01f, _radius);
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

    public bool ShouldTriggerOnSide(bool isHeads) => ShouldTrigger(isHeads);
}
