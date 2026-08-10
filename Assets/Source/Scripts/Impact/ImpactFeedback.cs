using UnityEngine;

public class ImpactFeedback : MonoBehaviour
{
    [Header("Source")]
    public CapTurnResolver TurnResolver;
    public CapFieldBoundary FieldBoundary;

    [Header("Audio")]
    public AudioClip TableImpactSound;
    public AudioClip CapImpactSound;
    [Tooltip("Abstract sound for a cap disappearing after it fell off the field.")]
    public AudioClip CapVanishSound;
    [Range(0f, 1f)] public float CapVanishVolume = 1f;
    [Range(0f, 2f)] public float BasePitch = 1f;
    [Range(0f, 0.5f)] public float PitchStepPerDepth = 0.1f;
    [Range(0f, 3f)] public float MaxPitch = 2f;

    [Header("VFX")]
    public GameObject TableImpactVFX;
    public GameObject CapImpactVFX;

    [Header("Camera Shake")]
    public float ShakeBaseAmount = 0.05f;
    public float ShakePerDepth = 0.03f;
    public float ShakeDuration = 0.25f;

    [Header("Hit-Stop")]
    public float HitStopBaseDuration = 0.04f;
    public float HitStopPerDepth = 0.02f;
    public float HitStopMaxDuration = 0.15f;

    private float _hitStopTimer;

    void OnEnable()
    {
        ResolveReferences();
        if (TurnResolver != null)
        {
            TurnResolver.OnTableImpact += HandleTableImpact;
            TurnResolver.OnCapImpact += HandleCapImpact;
        }
        if (FieldBoundary != null)
            FieldBoundary.OnFallingCapVanished += HandleFallingCapVanished;
    }

    void OnDisable()
    {
        if (TurnResolver != null)
        {
            TurnResolver.OnTableImpact -= HandleTableImpact;
            TurnResolver.OnCapImpact -= HandleCapImpact;
        }
        if (FieldBoundary != null)
            FieldBoundary.OnFallingCapVanished -= HandleFallingCapVanished;
        
        Time.timeScale = 1f;
    }

    void ResolveReferences()
    {
        if (TurnResolver == null)
            TurnResolver = FindFirstObjectByType<CapTurnResolver>();

        if (FieldBoundary == null)
            FieldBoundary = FindFirstObjectByType<CapFieldBoundary>();
    }

    void HandleTableImpact(Vector3 pos, float force)
    {
        if (AudioManager.Instance != null && TableImpactSound != null)
            AudioManager.Instance.Play3D(TableImpactSound, pos, BasePitch);

        if (VFXManager.Instance != null && TableImpactVFX != null)
            VFXManager.Instance.Spawn(TableImpactVFX, pos);
    }

    void HandleCapImpact(Vector3 pos, float force, int chainDepth)
    {
        float pitch = Mathf.Min(BasePitch + chainDepth * PitchStepPerDepth, MaxPitch);
        if (AudioManager.Instance != null && CapImpactSound != null)
            AudioManager.Instance.Play3D(CapImpactSound, pos, pitch);

        if (VFXManager.Instance != null && CapImpactVFX != null)
            VFXManager.Instance.Spawn(CapImpactVFX, pos);

        if (CameraShake.Instance != null)
        {
            float shakeAmount = ShakeBaseAmount + chainDepth * ShakePerDepth;
            CameraShake.Instance.Shake(shakeAmount, ShakeDuration);
        }

        _hitStopTimer = Mathf.Min(HitStopBaseDuration + chainDepth * HitStopPerDepth, HitStopMaxDuration);
    }

    void HandleFallingCapVanished(Vector3 pos)
    {
        if (CapVanishSound == null) return;
        if (AudioManager.Instance != null)
            AudioManager.Instance.Play3D(CapVanishSound, pos, BasePitch);
    }

    void Update()
    {
        if (_hitStopTimer > 0f)
        {
            _hitStopTimer -= Time.unscaledDeltaTime;
            Time.timeScale = 0f;
        }
        else
        {
            Time.timeScale = 1f;
        }
    }
}