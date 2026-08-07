using UnityEngine;

public class ImpactFeedback : MonoBehaviour
{
    [Header("Source")]
    public CapTurnResolver TurnResolver;

    [Header("Audio")]
    public AudioClip TableImpactSound;
    public AudioClip CapImpactSound;
    [Range(0f, 2f)] public float BasePitch = 1f;
    [Range(0f, 0.5f)] public float PitchStepPerDepth = 0.1f;
    [Range(0f, 0.5f)] public float PitchRandom = 0.05f;
    [Range(0f, 3f)] public float MaxPitch = 2f;
    [Range(1, 32)] public int AudioPoolSize = 8;
    [Min(0.1f)] public float SoundMaxDistance = 20f;

    [Header("VFX")]
    public GameObject TableImpactVFX;
    public GameObject CapImpactVFX;
    public float VFXLifetime = 1f;

    [Header("Camera Shake")]
    public Transform CameraTransform;
    public float ShakeBaseAmount = 0.05f;
    public float ShakePerDepth = 0.03f;
    public float ShakeDuration = 0.25f;

    [Header("Hit-Stop")]
    public float HitStopBaseDuration = 0.04f;
    public float HitStopPerDepth = 0.02f;
    public float HitStopMaxDuration = 0.15f;

    private AudioSource[] _audioPool;
    private int _audioPoolIndex;
    private Vector3 _cameraOriginalLocalPos;
    private float _shakeTimer;
    private float _shakeAmount;
    private float _hitStopTimer;

    void Awake()
    {
        ResolveReferences();
    }

    void Start()
    {
        if (CameraTransform == null) CameraTransform = Camera.main?.transform;
        if (CameraTransform != null)
            _cameraOriginalLocalPos = CameraTransform.localPosition;

        SetupAudioPool();
    }

    void ResolveReferences()
    {
        if (TurnResolver == null)
            TurnResolver = FindFirstObjectByType<CapTurnResolver>();
    }

    void SetupAudioPool()
    {
        _audioPool = new AudioSource[AudioPoolSize];
        for (int i = 0; i < AudioPoolSize; i++)
        {
            var obj = new GameObject($"ImpactAudio_{i}");
            obj.transform.SetParent(transform, false);
            var src = obj.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 1f;
            src.rolloffMode = AudioRolloffMode.Linear;
            src.maxDistance = SoundMaxDistance;
            src.dopplerLevel = 0f;
            _audioPool[i] = src;
        }
    }

    AudioSource GetPooledAudioSource(Vector3 pos)
    {
        var src = _audioPool[_audioPoolIndex];
        _audioPoolIndex = (_audioPoolIndex + 1) % AudioPoolSize;
        src.transform.position = pos;
        return src;
    }

    void OnEnable()
    {
        ResolveReferences();
        if (TurnResolver != null)
        {
            TurnResolver.OnTableImpact += HandleTableImpact;
            TurnResolver.OnCapImpact += HandleCapImpact;
        }
    }

    void OnDisable()
    {
        if (TurnResolver != null)
        {
            TurnResolver.OnTableImpact -= HandleTableImpact;
            TurnResolver.OnCapImpact -= HandleCapImpact;
        }
        Time.timeScale = 1f;
    }

    void HandleTableImpact(Vector3 pos, float force)
    {
        if (TableImpactSound != null)
        {
            var src = GetPooledAudioSource(pos);
            src.pitch = BasePitch;
            src.PlayOneShot(TableImpactSound);
        }
        SpawnVFX(TableImpactVFX, pos);
    }

    void HandleCapImpact(Vector3 pos, float force, int chainDepth)
    {
        float pitch = Mathf.Min(BasePitch + chainDepth * PitchStepPerDepth + Random.Range(-PitchRandom, PitchRandom), MaxPitch);
        if (CapImpactSound != null)
        {
            var src = GetPooledAudioSource(pos);
            src.pitch = pitch;
            src.PlayOneShot(CapImpactSound);
        }
        SpawnVFX(CapImpactVFX, pos);

        _shakeAmount = ShakeBaseAmount + chainDepth * ShakePerDepth;
        _shakeTimer = ShakeDuration;

        _hitStopTimer = Mathf.Min(HitStopBaseDuration + chainDepth * HitStopPerDepth, HitStopMaxDuration);
    }

    void SpawnVFX(GameObject prefab, Vector3 pos)
    {
        if (prefab == null) return;
        var vfx = Instantiate(prefab, pos, Quaternion.identity);
        Destroy(vfx, VFXLifetime);
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

        if (_shakeTimer > 0f && CameraTransform != null)
        {
            _shakeTimer -= Time.unscaledDeltaTime;
            Vector3 offset = Random.insideUnitSphere * _shakeAmount;
            CameraTransform.localPosition = _cameraOriginalLocalPos + offset;
        }
        else if (CameraTransform != null && CameraTransform.localPosition != _cameraOriginalLocalPos)
        {
            CameraTransform.localPosition = _cameraOriginalLocalPos;
        }
    }
}
