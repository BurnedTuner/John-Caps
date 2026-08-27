using UnityEngine;

/// <summary>
/// Manages a pool of AudioSources for playing sound effects. Singleton that
/// persists across scenes via DontDestroyOnLoad.
///
/// Play3D — spatial sound for world-space events (cap impacts, VFX).
/// Play2D — non-spatial sound for UI button clicks.
///
/// VOLUME: Play3D/Play2D use the `volume` parameter AS-IS (no GameSettings
/// multiplier). The SFX volume slider in GameSettings controls
/// AudioListener.volume (master SFX) — a global multiplier applied by Unity
/// to ALL audio sources. This gives the designer full control over per-clip
/// volume (via the `volume` param + AudioClip import settings), while the
/// player's SFX slider scales everything uniformly.
/// </summary>
public class AudioManager : MonoBehaviour
{
    public static AudioManager Instance { get; private set; }

    [Range(1, 32)] public int PoolSize = 8;
    [Min(0.1f)] public float MaxDistance = 20f;

    private AudioSource[] _audioPool;
    private int _poolIndex;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void Start()
    {
        SetupPool();
        // Apply the persisted SFX volume to AudioListener on startup.
        ApplySfxVolume();
    }

    void SetupPool()
    {
        _audioPool = new AudioSource[PoolSize];
        for (int i = 0; i < PoolSize; i++)
        {
            var obj = new GameObject($"AudioSource_{i}");
            obj.transform.SetParent(transform, false);
            var src = obj.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 1f;
            src.rolloffMode = AudioRolloffMode.Linear;
            src.maxDistance = MaxDistance;
            src.dopplerLevel = 0f;
            _audioPool[i] = src;
        }
    }

    public void Play3D(AudioClip clip, Vector3 pos, float pitch = 1f, float volume = 1f)
    {
        if (clip == null) return;
        var src = _audioPool[_poolIndex];
        _poolIndex = (_poolIndex + 1) % PoolSize;
        src.transform.position = pos;
        src.spatialBlend = 1f;
        src.pitch = pitch;
        // Use volume AS-IS — the player's SFX slider is applied globally via
        // AudioListener.volume (set in ApplySfxVolume). This gives the designer
        // full control over per-clip volume.
        src.PlayOneShot(clip, Mathf.Clamp01(volume));
    }

    /// <summary>
    /// Plays a 2D (non-spatial) sound — used for UI button clicks.
    /// </summary>
    public void Play2D(AudioClip clip, float pitch = 1f, float volume = 1f)
    {
        if (clip == null) return;
        var src = _audioPool[_poolIndex];
        _poolIndex = (_poolIndex + 1) % PoolSize;
        src.spatialBlend = 0f;
        src.pitch = pitch;
        src.PlayOneShot(clip, Mathf.Clamp01(volume));
    }

    /// <summary>
    /// Applies the SFX volume from GameSettings to AudioListener.volume.
    /// This is the global multiplier — affects ALL audio sources (SFX + BGM
    /// if BGM uses AudioSource). Called on Start and when the player changes
    /// the SFX slider.
    /// </summary>
    public void ApplySfxVolume()
    {
        float sfxVolume = GameSettings.GetSfxVolume();
        AudioListener.volume = sfxVolume;
    }
}
