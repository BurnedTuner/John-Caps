using UnityEngine;

public class ImpactFeedback : MonoBehaviour
{
    [Header("Audio")]
    public AudioClip TableImpactSound;
    public AudioClip CapImpactSound;
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

    private CapTurnResolver _turnResolver;
    private float _hitStopTimer;

    void Start()
    {
        _turnResolver = FindFirstObjectByType<CapTurnResolver>();
        if (_turnResolver != null)
        {
            _turnResolver.OnTableImpact += HandleTableImpact;
            _turnResolver.OnCapImpact += HandleCapImpact;
        }
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

    void OnDisable()
    {
        if (_turnResolver != null)
        {
            _turnResolver.OnTableImpact -= HandleTableImpact;
            _turnResolver.OnCapImpact -= HandleCapImpact;
        }
        Time.timeScale = 1f;
    }
}
