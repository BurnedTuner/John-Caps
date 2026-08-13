using UnityEngine;

/// <summary>
/// Which side the bomb must land on to trigger.
/// </summary>
public enum BombTriggerSide
{
    Heads = 0,
    Tails = 1,
    Either = 2
}

/// <summary>
/// Launches nearby caps away from this cap when it finishes a flip OR when it
/// lands from a hand throw on the trigger side.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Cap))]
public sealed class BombCapFlipEffect : CapFlipEffect
{
    [Header("Trigger")]
    [Tooltip("Which side the bomb must land on to trigger the explosion. " +
             "Heads = bomb explodes when it lands heads-up. Tails = explodes when tails-up. " +
             "Either = explodes on any landing.")]
    public BombTriggerSide TriggerSide = BombTriggerSide.Heads;

    [SerializeField, Min(0.01f)]
    [Tooltip("Explosion radius on the XZ plane, measured between cap centres.")]
    private float _radius = 3f;

    [SerializeField, Min(0f)]
    [Tooltip("Flat launch force applied equally to every available cap inside Radius.")]
    private float _force = 3f;

    public float Radius => _radius;
    public float Force => _force;

    [Header("Feedback")]
    public GameObject ExplosionVFX;
    public AudioClip ExplosionSound;
    [Range(0f, 2f)] public float ExplosionPitch = 0.8f;
    [Range(0f, 1f)] public float ExplosionVolume = 1f;
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
        if (flipEvent.Source == null || _radius <= 0f || _force <= 0f) return;
        if (!ShouldTrigger(flipEvent.Source.IsHeads)) return;
        commands.Add(new RadialLaunchCommand(flipEvent.Source, flipEvent.Position, _radius, _force));
    }

    /// <summary>
    /// Returns true if the bomb should explode given the side it landed on.
    /// </summary>
    public bool ShouldTrigger(bool landedHeads)
    {
        return TriggerSide switch
        {
            BombTriggerSide.Heads => landedHeads,
            BombTriggerSide.Tails => !landedHeads,
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
        radius = _radius;
        force = _force;
        return _radius > 0f && _force > 0f;
    }

    void OnValidate()
    {
        _radius = Mathf.Max(0.01f, _radius);
        _force = Mathf.Max(0f, _force);
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
}