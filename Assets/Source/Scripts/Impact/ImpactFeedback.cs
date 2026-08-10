using UnityEngine;

public class ImpactFeedback : MonoBehaviour
{
    [Header("Source")]
    public CapTurnResolver TurnResolver;
    public CapFieldBoundary FieldBoundary;

    [Header("Audio - Table Impact")]
    public AudioClip TableImpactSound;
    [Range(0f, 2f)] public float TableImpactPitch = 1f;
    [Range(0f, 1f)] public float TableImpactVolume = 1f;

    [Header("Audio - Cap Impact")]
    public AudioClip CapImpactSound;
    [Range(0f, 2f)] public float CapImpactBasePitch = 1f;
    [Range(0f, 0.5f)] public float CapImpactPitchStepPerDepth = 0.1f;
    [Range(0f, 3f)] public float CapImpactMaxPitch = 2f;
    [Range(0f, 1f)] public float CapImpactVolume = 1f;

    [Header("Audio - Stack")]
    public AudioClip StackSound;
    [Range(0f, 2f)] public float StackBasePitch = 1f;
    [Range(0f, 0.5f)] public float StackPitchStepPerStack = 0.05f;
    [Range(0f, 3f)] public float StackMaxPitch = 2f;
    [Range(0f, 1f)] public float StackVolume = 1f;

    [Header("Audio - Cap Vanish")]
    [Tooltip("Abstract sound for a cap disappearing after it fell off the field.")]
    public AudioClip CapVanishSound;
    [Range(0f, 2f)] public float CapVanishPitch = 1f;
    [Range(0f, 1f)] public float CapVanishVolume = 1f;

    [Header("VFX")]
    public GameObject TableImpactVFX;
    public GameObject CapImpactVFX;
    public GameObject StackVFX;
    [Min(0.1f)] public float StackVFXLifetime = 0.8f;

    [Header("Camera Shake")]
    public float ShakeBaseAmount = 0.05f;
    public float ShakePerDepth = 0.03f;
    public float ShakeDuration = 0.25f;
    [Range(0f, 0.2f)] public float StackShakeAmount = 0.02f;
    [Range(0f, 0.5f)] public float StackShakeDuration = 0.1f;

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
            TurnResolver.OnCapStacked += HandleCapStacked;
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
            TurnResolver.OnCapStacked -= HandleCapStacked;
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
            AudioManager.Instance.Play3D(TableImpactSound, pos, TableImpactPitch, TableImpactVolume);

        if (VFXManager.Instance != null && TableImpactVFX != null)
            VFXManager.Instance.Spawn(TableImpactVFX, pos);
    }

    void HandleCapImpact(Vector3 pos, float force, int chainDepth)
    {
        float pitch = Mathf.Min(CapImpactBasePitch + chainDepth * CapImpactPitchStepPerDepth, CapImpactMaxPitch);
        if (AudioManager.Instance != null && CapImpactSound != null)
            AudioManager.Instance.Play3D(CapImpactSound, pos, pitch, CapImpactVolume);

        if (VFXManager.Instance != null && CapImpactVFX != null)
            VFXManager.Instance.Spawn(CapImpactVFX, pos);

        if (CameraShake.Instance != null)
        {
            float shakeAmount = ShakeBaseAmount + chainDepth * ShakePerDepth;
            CameraShake.Instance.Shake(shakeAmount, ShakeDuration);
        }

        _hitStopTimer = Mathf.Min(HitStopBaseDuration + chainDepth * HitStopPerDepth, HitStopMaxDuration);
    }

    void HandleCapStacked(Vector3 pos, float force, int stackCount)
    {
        int step = Mathf.Max(0, stackCount - 1);
        float pitch = Mathf.Min(StackBasePitch + step * StackPitchStepPerStack, StackMaxPitch);

        if (AudioManager.Instance != null && StackSound != null)
            AudioManager.Instance.Play3D(StackSound, pos, pitch, StackVolume);

        if (VFXManager.Instance != null && StackVFX != null)
            VFXManager.Instance.Spawn(StackVFX, pos, StackVFXLifetime);

        if (CameraShake.Instance != null && StackShakeAmount > 0f && StackShakeDuration > 0f)
            CameraShake.Instance.Shake(StackShakeAmount, StackShakeDuration);
    }

    void HandleFallingCapVanished(Vector3 pos)
    {
        if (CapVanishSound == null) return;
        if (AudioManager.Instance != null)
            AudioManager.Instance.Play3D(CapVanishSound, pos, CapVanishPitch, CapVanishVolume);
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