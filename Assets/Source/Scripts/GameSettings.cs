using UnityEngine;

/// <summary>
/// Global game settings singleton. Stores player preferences like volume
/// sliders and precision aim toggle. Place on a persistent GameObject in the scene.
///
/// The PauseMenu UI binds its sliders/toggles to the Set* methods.
/// AudioManager and CapThrower read the values at runtime.
/// </summary>
public class GameSettings : MonoBehaviour
{
    public static GameSettings Instance { get; private set; }

    [Header("Audio")]
    [Range(0f, 1f)] public float SfxVolume = 1f;
    [Range(0f, 1f)] public float BgmVolume = 0.5f;

    [Header("Precision Aim Mode")]
    [Tooltip("If true, releasing LMB while aiming enters precision mode: WASD nudges the " +
             "aim point (camera-style acceleration curve), Space confirms the throw, " +
             "ESC cancels. If false, releasing LMB throws immediately. " +
             "Toggled at runtime via the on-screen UI or the Q key.")]
    public bool PrecisionAimEnabled = false;

    [Header("Background Music")]
    [Tooltip("Audio clip for background music. Played on a looping AudioSource.")]
    public AudioClip BgmClip;

    private AudioSource _bgmSource;

    void Awake()
    {
        Instance = this;
    }

    void Start()
    {
        // Set up background music AudioSource.
        _bgmSource = gameObject.AddComponent<AudioSource>();
        _bgmSource.loop = true;
        _bgmSource.playOnAwake = false;
        _bgmSource.spatialBlend = 0f; // 2D
        if (BgmClip != null)
        {
            _bgmSource.clip = BgmClip;
            _bgmSource.volume = BgmVolume;
            _bgmSource.Play();
        }
    }

    /// <summary>Called by the SFX volume slider. Sets the global SFX volume.</summary>
    public void SetSfxVolume(float value)
    {
        SfxVolume = Mathf.Clamp01(value);
    }

    /// <summary>Called by the BGM volume slider. Sets the BGM AudioSource volume.</summary>
    public void SetBgmVolume(float value)
    {
        BgmVolume = Mathf.Clamp01(value);
        if (_bgmSource != null)
            _bgmSource.volume = BgmVolume;
    }

    /// <summary>Called by the precision-mode UI toggle. Enables/disables precision aim mode.</summary>
    public void SetPrecisionAimEnabled(bool value)
    {
        PrecisionAimEnabled = value;
    }

    /// <summary>The current SFX volume (0-1). AudioManager reads this when playing sounds.</summary>
    public static float GetSfxVolume() => Instance != null ? Instance.SfxVolume : 1f;

    /// <summary>The current BGM volume (0-1).</summary>
    public static float GetBgmVolume() => Instance != null ? Instance.BgmVolume : 0.5f;

    /// <summary>True if precision aim mode is enabled (WASD nudge after LMB release).</summary>
    public static bool IsPrecisionAimEnabled() => Instance != null ? Instance.PrecisionAimEnabled : false;
}
