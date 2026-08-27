using UnityEngine;

/// <summary>
/// Global game settings singleton. Stores player preferences like volume
/// sliders and precision aim toggle. Place on a persistent GameObject in the scene.
///
/// The PauseMenu UI binds its sliders/toggles to the Set* methods.
/// AudioManager and CapThrower read the values at runtime.
///
/// PERSISTENCE:
///   - PrecisionAimEnabled, SfxVolume, BgmVolume are all saved to PlayerPrefs
///     so they survive scene transitions AND game restarts. Loaded on Awake.
///   - The GameObject is DontDestroyOnLoad so the BGM AudioSource persists
///     across scenes (no music restart on level load). A singleton guard
///     prevents duplicates when multiple scenes have a GameSettings object.
///
/// SLIDER BINDING:
///   Any scene with a volume slider (pause menu, main menu, options screen)
///   should read GameSettings.GetSfxVolume() / GetBgmVolume() on Start to
///   initialize the slider value, and call SetSfxVolume / SetBgmVolume on
///   slider change. PauseMenu already does this — see PauseMenu.Start.
/// </summary>
public class GameSettings : MonoBehaviour
{
    public static GameSettings Instance { get; private set; }

    [Header("Audio")]
    [Range(0f, 1f)] public float SfxVolume = 1f;
    [Range(0f, 1f)] public float BgmVolume = 0.5f;

    [Header("BGM volume range")]
    [Tooltip("Minimum BGM volume when the slider is at 0. Prevents music from being completely silent. " +
             "E.g., 0.1 = even at slider 0, music plays at 10% volume.")]
    [Range(0f, 0.5f)] public float BgmMinVolume = 0.1f;

    [Tooltip("Maximum BGM volume when the slider is at 1. Caps the music volume so it doesn't " +
             "overpower SFX. E.g., 0.5 = even at slider 1, music caps at 50% volume.")]
    [Range(0.1f, 1f)] public float BgmMaxVolume = 0.5f;

    [Header("Precision Aim Mode")]
    [Tooltip("If true, releasing LMB while aiming enters precision mode: WASD nudges the " +
             "aim point (camera-style acceleration curve), Space confirms the throw, " +
             "ESC cancels. If false, releasing LMB throws immediately. " +
             "Toggled at runtime via the on-screen UI or the Q key. " +
             "PERSISTED across scenes and game restarts via PlayerPrefs.")]
    public bool PrecisionAimEnabled = false;

    [Header("Background Music")]
    [Tooltip("Audio clip for background music. Played on a looping AudioSource.")]
    public AudioClip BgmClip;

    private AudioSource _bgmSource;

    const string PrecisionAimPrefKey = "PrecisionAimEnabled";
    const string SfxVolumePrefKey = "SfxVolume";
    const string BgmVolumePrefKey = "BgmVolume";

    void Awake()
    {
        // Singleton guard — if an instance already exists (from a previous
        // scene that had GameSettings + DontDestroyOnLoad), destroy this
        // duplicate. The original survives.
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        // Load all persisted settings from PlayerPrefs. If a key doesn't exist
        // (first run), defaults to the field's serialized value. This survives
        // scene transitions AND game restarts — the setting persists until the
        // player changes it.
        PrecisionAimEnabled = PlayerPrefs.GetInt(PrecisionAimPrefKey, PrecisionAimEnabled ? 1 : 0) == 1;
        SfxVolume = PlayerPrefs.GetFloat(SfxVolumePrefKey, SfxVolume);
        BgmVolume = PlayerPrefs.GetFloat(BgmVolumePrefKey, BgmVolume);
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
            // Map persisted slider value to the actual volume range.
            _bgmSource.volume = Mathf.Lerp(BgmMinVolume, BgmMaxVolume, BgmVolume);
            _bgmSource.Play();
        }
    }

    /// <summary>
    /// Called by the SFX volume slider. Sets the global SFX volume (applied to
    /// AudioListener.volume — Unity's master multiplier for ALL audio) and saves
    /// it to PlayerPrefs. Applied immediately via AudioManager.ApplySfxVolume().
    /// </summary>
    public void SetSfxVolume(float value)
    {
        SfxVolume = Mathf.Clamp01(value);
        PlayerPrefs.SetFloat(SfxVolumePrefKey, SfxVolume);
        PlayerPrefs.Save();
        // Apply immediately so the player hears the change.
        if (AudioManager.Instance != null)
            AudioManager.Instance.ApplySfxVolume();
        else
            AudioListener.volume = SfxVolume;
    }

    /// <summary>
    /// Called by the BGM volume slider. Sets the BGM AudioSource volume (mapped
    /// from slider 0-1 to BgmMinVolume-BgmMaxVolume range) and saves the raw
    /// slider value to PlayerPrefs.
    /// </summary>
    public void SetBgmVolume(float value)
    {
        BgmVolume = Mathf.Clamp01(value);
        // Map slider [0,1] → [BgmMinVolume, BgmMaxVolume] so the designer
        // controls the actual volume range. At slider 0, music is still audible
        // (BgmMinVolume). At slider 1, music doesn't overpower (BgmMaxVolume).
        float actualVolume = Mathf.Lerp(BgmMinVolume, BgmMaxVolume, BgmVolume);
        if (_bgmSource != null)
            _bgmSource.volume = actualVolume;
        PlayerPrefs.SetFloat(BgmVolumePrefKey, BgmVolume);
        PlayerPrefs.Save();
    }

    /// <summary>
    /// Called by the precision-mode UI toggle (or the Q key). Enables/disables
    /// precision aim mode. Saves the setting to PlayerPrefs so it persists
    /// across scenes AND game restarts.
    /// </summary>
    public void SetPrecisionAimEnabled(bool value)
    {
        PrecisionAimEnabled = value;
        PlayerPrefs.SetInt(PrecisionAimPrefKey, value ? 1 : 0);
        PlayerPrefs.Save();
    }

    /// <summary>The current SFX volume (0-1). AudioManager reads this when playing sounds.</summary>
    public static float GetSfxVolume() => Instance != null ? Instance.SfxVolume : 1f;

    /// <summary>The current BGM volume (0-1).</summary>
    public static float GetBgmVolume() => Instance != null ? Instance.BgmVolume : 0.5f;

    /// <summary>True if precision aim mode is enabled (WASD nudge after LMB release).</summary>
    public static bool IsPrecisionAimEnabled() => Instance != null ? Instance.PrecisionAimEnabled : false;
}
