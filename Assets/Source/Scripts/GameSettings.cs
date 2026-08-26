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
            // Use the persisted BgmVolume (loaded in Awake).
            _bgmSource.volume = BgmVolume;
            _bgmSource.Play();
        }
    }

    /// <summary>
    /// Called by the SFX volume slider. Sets the global SFX volume and saves
    /// it to PlayerPrefs so it persists across scenes and game restarts.
    /// </summary>
    public void SetSfxVolume(float value)
    {
        SfxVolume = Mathf.Clamp01(value);
        PlayerPrefs.SetFloat(SfxVolumePrefKey, SfxVolume);
        PlayerPrefs.Save();
    }

    /// <summary>
    /// Called by the BGM volume slider. Sets the BGM AudioSource volume and
    /// saves it to PlayerPrefs so it persists across scenes and game restarts.
    /// </summary>
    public void SetBgmVolume(float value)
    {
        BgmVolume = Mathf.Clamp01(value);
        if (_bgmSource != null)
            _bgmSource.volume = BgmVolume;
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
