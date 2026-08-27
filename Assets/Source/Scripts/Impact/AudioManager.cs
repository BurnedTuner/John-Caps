using UnityEngine;

public class AudioManager : MonoBehaviour
{
    public static AudioManager Instance { get; private set; }

    [Range(1, 32)] public int PoolSize = 8;
    [Min(0.1f)] public float MaxDistance = 20f;

    private AudioSource[] _audioPool;
    private int _poolIndex;

    void Awake() => Instance = this;

    void Start() => SetupPool();

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
        src.pitch = pitch;
        src.spatialBlend = 1f;
        // Multiply the per-sound volume by the global SFX volume from GameSettings.
        float sfxVolume = GameSettings.GetSfxVolume();
        src.PlayOneShot(clip, Mathf.Clamp01(volume * sfxVolume));
    }

    /// <summary>
    /// Plays a 2D (non-spatial) sound — used for UI button clicks. Uses the same
    /// pool as Play3D but sets spatialBlend to 0 so the sound plays at full
    /// volume regardless of position.
    /// </summary>
    public void Play2D(AudioClip clip, float pitch = 1f, float volume = 1f)
    {
        if (clip == null) return;
        var src = _audioPool[_poolIndex];
        _poolIndex = (_poolIndex + 1) % PoolSize;
        src.spatialBlend = 0f;
        src.pitch = pitch;
        float sfxVolume = GameSettings.GetSfxVolume();
        src.PlayOneShot(clip, Mathf.Clamp01(volume * sfxVolume));
    }
}