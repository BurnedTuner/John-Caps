using UnityEngine;

/// <summary>
/// Launches nearby caps away from this cap when it finishes a flip.
/// Bomb levels are prefab variants with different Radius and Force values.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Cap))]
public sealed class BombCapFlipEffect : CapFlipEffect
{
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
        commands.Add(new RadialLaunchCommand(flipEvent.Source, flipEvent.Position, _radius, _force));
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