using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Lightweight slider binder for GameSettings. Place this on any GameObject
/// in any scene (main menu, options screen, pause menu) that has SFX/BGM
/// volume sliders. On Start, reads the persisted values from GameSettings
/// (which loaded them from PlayerPrefs on its Awake) and sets the sliders.
/// Wires the sliders' onValueChanged to GameSettings.SetSfxVolume /
/// SetBgmVolume, which save to PlayerPrefs.
///
/// Unlike PauseMenu, this component does NOT handle ESC or panel toggling —
/// it's purely for slider binding. Use it when you want a settings panel
/// that's always visible (e.g., main menu, options screen).
///
/// Setup:
/// 1. Add this component to a GameObject in the scene.
/// 2. Assign _sfxSlider and/or _bgmSlider (UI Slider components, 0-1 range).
/// 3. The sliders' values are set on Start to match GameSettings.
/// 4. When the player drags a slider, GameSettings.SetSfxVolume / SetBgmVolume
///    is called, which saves to PlayerPrefs.
/// </summary>
public class SettingsBinder : MonoBehaviour
{
    [Header("Sliders")]
    [Tooltip("SFX volume slider (0-1). If null, SFX binding is skipped.")]
    [SerializeField] private Slider _sfxSlider;

    [Tooltip("BGM volume slider (0-1). If null, BGM binding is skipped.")]
    [SerializeField] private Slider _bgmSlider;

    void Start()
    {
        if (GameSettings.Instance == null)
        {
            Debug.LogWarning("[SettingsBinder] No GameSettings instance found. " +
                             "Make sure a GameSettings object exists in the scene (or persisted via DontDestroyOnLoad).", this);
            return;
        }

        if (_sfxSlider != null)
        {
            // Set the slider value to the persisted value (without firing onValueChanged).
            _sfxSlider.SetValueWithoutNotify(GameSettings.Instance.SfxVolume);
            _sfxSlider.onValueChanged.AddListener(GameSettings.Instance.SetSfxVolume);
        }

        if (_bgmSlider != null)
        {
            _bgmSlider.SetValueWithoutNotify(GameSettings.Instance.BgmVolume);
            _bgmSlider.onValueChanged.AddListener(GameSettings.Instance.SetBgmVolume);
        }
    }

    void OnDestroy()
    {
        // Unbind to prevent the listener from firing after this component is destroyed
        // (e.g., when the scene unloads). Without this, the slider's onValueChanged
        // would reference a destroyed GameSettings if GameSettings was also destroyed
        // — but since GameSettings is DontDestroyOnLoad, it persists. Still, unbinding
        // is cleaner.
        if (GameSettings.Instance != null)
        {
            if (_sfxSlider != null)
                _sfxSlider.onValueChanged.RemoveListener(GameSettings.Instance.SetSfxVolume);
            if (_bgmSlider != null)
                _bgmSlider.onValueChanged.RemoveListener(GameSettings.Instance.SetBgmVolume);
        }
    }
}
