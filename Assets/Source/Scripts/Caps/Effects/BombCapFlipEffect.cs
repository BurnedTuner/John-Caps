using UnityEngine;

/// <summary>
/// Which side the bomb must land on to trigger.
/// </summary>
public enum BombTriggerSide
{
    Face = 0,
    Back = 1,
    Either = 2
}

/// <summary>
/// Launches nearby caps away from this cap when it finishes a flip OR when it
/// lands from a hand throw on the trigger side.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Cap))]
public sealed class BombCapFlipEffect : CapFlipEffect, ICapEffectRadius, ICapAbility
{
    [Header("Sticker")]
    [Tooltip("Icon sprite shown as a sticker above the cap.")]
    [SerializeField] private Sprite _stickerSprite;

    [Header("Level")]
    [Tooltip("Ability level (1-3). Higher levels = stronger parameters.")]
    [Range(1, 3)] [SerializeField] private int _level = 1;

    [Header("Level Parameters")]
    [Tooltip("Radius at level 1.")]
    [SerializeField] private float _radiusL1 = 3f;
    [Tooltip("Radius at level 2.")]
    [SerializeField] private float _radiusL2 = 4f;
    [Tooltip("Radius at level 3.")]
    [SerializeField] private float _radiusL3 = 5f;
    [Tooltip("Force at level 1.")]
    [SerializeField] private float _forceL1 = 3f;
    [Tooltip("Force at level 2.")]
    [SerializeField] private float _forceL2 = 4f;
    [Tooltip("Force at level 3.")]
    [SerializeField] private float _forceL3 = 5f;

    public Sprite StickerSprite => _stickerSprite;
    public int Level => _level;
    public string Description =>
        $"Когда падает лицом вверх,\nОТТАЛКИВАЕТ всех в радиусе {Radius:F0}.";

    public float Radius => _level switch { 2 => _radiusL2, 3 => _radiusL3, _ => _radiusL1 };
    public float Force => _level switch { 2 => _forceL2, 3 => _forceL3, _ => _forceL1 };

    public float EffectRadius => Radius;

    /// <summary>The bomb's push force. Used by the trajectory preview to predict where affected caps will land.</summary>
    public float EffectForce => Force;

    public Color ZoneColor => Color.red;

    /// <summary>
    /// Sets the ability level. Called by CapFactory.CreateComposed after the
    /// component is added to a cap instance (parameters come from CopyFrom).
    /// </summary>
    public void SetLevel(int level) => _level = Mathf.Clamp(level, 1, 3);

    /// <summary>
    /// Copies all serialized parameters (sticker, level parameters, trigger side,
    /// VFX, material change) from the given template's BombCapFlipEffect.
    /// Used by CapFactory.CreateComposed to apply a deck's bomb template parameters
    /// to a dynamically-added bomb component on a cap instance.
    /// </summary>
    public void CopyFrom(BombCapFlipEffect source)
    {
        if (source == null) return;
        _stickerSprite = source._stickerSprite;
        _radiusL1 = source._radiusL1;
        _radiusL2 = source._radiusL2;
        _radiusL3 = source._radiusL3;
        _forceL1 = source._forceL1;
        _forceL2 = source._forceL2;
        _forceL3 = source._forceL3;
        TriggerSide = source.TriggerSide;
        ExplosionVFX = source.ExplosionVFX;
        ExplosionSound = source.ExplosionSound;
        ExplosionPitch = source.ExplosionPitch;
        ExplosionVolume = source.ExplosionVolume;
        ExplosionShakeAmount = source.ExplosionShakeAmount;
        ExplosionShakeDuration = source.ExplosionShakeDuration;
        ExplosionMaterial = source.ExplosionMaterial;
        MaterialChangeDuration = source.MaterialChangeDuration;
    }

    [Header("Trigger")]
    [Tooltip("Which side the bomb must land on to trigger the explosion.")]
    public BombTriggerSide TriggerSide = BombTriggerSide.Face;

    [Header("Feedback")]
    public GameObject ExplosionVFX;
    public AudioClip ExplosionSound;
    [Range(0f, 2f)] public float ExplosionPitch = 0.8f;
    public float ExplosionVolume = 1f;
    public float ExplosionShakeAmount = 0.4f;
    public float ExplosionShakeDuration = 0.6f;

    [Header("Material Change")]
    [Tooltip("Material to switch to when the bomb explodes.")]
    public Material ExplosionMaterial;
    [Tooltip("How long (in seconds) to stay in the explosion material.")]
    public float MaterialChangeDuration = 0.5f;

    public override void BuildCommands(
        in CapFlipEvent flipEvent,
        ICapEffectQuery query,
        ICapEffectCommandSink commands)
    {
        if (flipEvent.Source == null || Radius <= 0f || Force <= 0f) return;
        if (!ShouldTrigger(flipEvent.Source.IsFace)) return;
        commands.Add(new RadialPushCommand(flipEvent.Source, flipEvent.Position, Radius, Force));
    }

    /// <summary>
    /// Returns true if the bomb should explode given the side it landed on.
    /// </summary>
    public bool ShouldTrigger(bool landedFace)
    {
        return TriggerSide switch
        {
            BombTriggerSide.Face => landedFace,
            BombTriggerSide.Back => !landedFace,
            BombTriggerSide.Either => true,
            _ => false,
        };
    }

    /// <summary>
    /// Describes this effect as a radial launch for the AI move search.
    /// Returns the explosion radius and force without needing a live cap.
    /// </summary>
    public override bool TryGetRadialLaunch(out float radius, out float force)
    {
        radius = Radius;
        force = Force;
        return Radius > 0f && Force > 0f;
    }

    void OnValidate()
    {
        _radiusL1 = Mathf.Max(0.01f, _radiusL1);
        _radiusL2 = Mathf.Max(0.01f, _radiusL2);
        _radiusL3 = Mathf.Max(0.01f, _radiusL3);
        _forceL1 = Mathf.Max(0f, _forceL1);
        _forceL2 = Mathf.Max(0f, _forceL2);
        _forceL3 = Mathf.Max(0f, _forceL3);
        _level = Mathf.Clamp(_level, 1, 3);
    }

    public override void PlayFeedback(Vector3 position, float force)
    {
        if (ExplosionVFX != null && VFXManager.Instance != null)
            VFXManager.Instance.Spawn(ExplosionVFX, position);

        if (ExplosionSound != null && AudioManager.Instance != null)
            AudioManager.Instance.Play3D(ExplosionSound, position, ExplosionPitch, ExplosionVolume);

        if (CameraShake.Instance != null)
            CameraShake.Instance.Shake(ExplosionShakeAmount, ExplosionShakeDuration);

        if (ExplosionMaterial != null)
        {
            Cap cap = GetComponent<Cap>();
            if (cap != null)
            {
                cap.SetOverrideMaterial(ExplosionMaterial, MaterialChangeDuration);
            }
        }
    }

    public bool ShouldTriggerOnSide(bool isFace) => ShouldTrigger(isFace);
}