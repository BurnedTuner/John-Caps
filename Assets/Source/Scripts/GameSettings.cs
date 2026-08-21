using UnityEngine;

/// <summary>
/// Global game settings singleton. Stores player preferences like volume
/// sliders and aim system toggle. Place on a persistent GameObject in the scene.
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

    [Header("Aim System")]
    [Tooltip("If true, uses the acceleration-based aim system (dead zone + velocity). " +
             "If false, uses the legacy aim system (aim point follows cursor exactly).")]
    public bool UseAccelerationAim = false;

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

    /// <summary>Called by the aim system toggle. Switches between acceleration and legacy aim.</summary>
    public void SetUseAccelerationAim(bool value)
    {
        UseAccelerationAim = value;
    }

    /// <summary>The current SFX volume (0-1). AudioManager reads this when playing sounds.</summary>
    public static float GetSfxVolume() => Instance != null ? Instance.SfxVolume : 1f;

    /// <summary>The current BGM volume (0-1).</summary>
    public static float GetBgmVolume() => Instance != null ? Instance.BgmVolume : 0.5f;

    /// <summary>True if the acceleration-based aim system is active.</summary>
    public static bool IsAccelerationAimEnabled() => Instance != null ? Instance.UseAccelerationAim : true;
}
