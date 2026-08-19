using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

/// <summary>
/// Pause menu controller. Shows/hides a pause panel when ESC is pressed.
/// Contains:
///   - SFX volume slider (binds to GameSettings.SetSfxVolume)
///   - BGM volume slider (binds to GameSettings.SetBgmVolume)
///   - Aim system toggle (binds to GameSettings.SetUseAccelerationAim)
///
/// Setup in Unity:
/// 1. Create a Canvas with a Panel (the pause menu).
/// 2. Add this component to the Panel (or any GameObject in the scene).
/// 3. Assign the Panel, SFX Slider, BGM Slider, and Aim Toggle in the inspector.
/// 4. The SFX slider's OnValueChanged → GameSettings.SetSfxVolume (dynamic float)
///    OR assign it here via the _sfxSlider reference (this script handles it).
/// 5. Set the panel inactive by default.
/// </summary>
public class PauseMenu : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The pause menu panel GameObject. Shown/hidden when ESC is pressed.")]
    [SerializeField] private GameObject _panel;

    [Tooltip("SFX volume slider (0-1).")]
    [SerializeField] private Slider _sfxSlider;

    [Tooltip("BGM volume slider (0-1).")]
    [SerializeField] private Slider _bgmSlider;

    [Tooltip("Toggle for aim system. True = acceleration aim, false = legacy (exact cursor).")]
    [SerializeField] private Toggle _aimToggle;

    [Header("Input")]
    [Tooltip("Key to toggle the pause menu.")]
    [SerializeField] private Key _toggleKey = Key.Escape;

    private bool _isPaused;

    void Start()
    {
        // Hide the panel on start.
        if (_panel != null)
            _panel.SetActive(false);

        // Initialize slider/toggle values from GameSettings.
        if (GameSettings.Instance != null)
        {
            if (_sfxSlider != null)
            {
                _sfxSlider.value = GameSettings.Instance.SfxVolume;
                _sfxSlider.onValueChanged.AddListener(GameSettings.Instance.SetSfxVolume);
            }
            if (_bgmSlider != null)
            {
                _bgmSlider.value = GameSettings.Instance.BgmVolume;
                _bgmSlider.onValueChanged.AddListener(GameSettings.Instance.SetBgmVolume);
            }
            if (_aimToggle != null)
            {
                _aimToggle.isOn = GameSettings.Instance.UseAccelerationAim;
                _aimToggle.onValueChanged.AddListener(GameSettings.Instance.SetUseAccelerationAim);
            }
        }
    }

    void Update()
    {
        if (Keyboard.current == null) return;

        if (Keyboard.current[_toggleKey].wasPressedThisFrame)
        {
            TogglePause();
        }
    }

    /// <summary>Toggle the pause menu on/off.</summary>
    public void TogglePause()
    {
        _isPaused = !_isPaused;

        if (_panel != null)
            _panel.SetActive(_isPaused);

        // Pause/unpause the game. When paused, set timeScale to 0 so the
        // simulation freezes. The aim system and UI still work (they use
        // unscaled time where needed).
        Time.timeScale = _isPaused ? 0f : 1f;
    }

    /// <summary>Force-close the pause menu (e.g., when a new turn starts).</summary>
    public void Close()
    {
        _isPaused = false;
        if (_panel != null)
            _panel.SetActive(false);
        Time.timeScale = 1f;
    }

    void OnDisable()
    {
        // Restore time scale if the component is disabled while paused.
        if (_isPaused)
        {
            Time.timeScale = 1f;
            _isPaused = false;
        }
    }
}
