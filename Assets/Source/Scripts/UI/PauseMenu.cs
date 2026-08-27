using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

/// <summary>
/// Pause menu controller. Shows/hides a pause panel when ESC is pressed.
/// Contains:
///   - SFX volume slider (binds to GameSettings.SetSfxVolume)
///   - BGM volume slider (binds to GameSettings.SetBgmVolume)
///   - Precision aim BUTTON (image swaps based on GameSettings.PrecisionAimEnabled)
///
/// Setup in Unity:
/// 1. Create a Canvas with a Panel (the pause menu).
/// 2. Add this component to the Panel (or any GameObject in the scene).
/// 3. Assign the Panel, SFX Slider, BGM Slider, Precision Aim Button, and
///    CapThrower in the inspector.
/// 4. The SFX slider's OnValueChanged → GameSettings.SetSfxVolume (dynamic float)
///    OR assign it here via the _sfxSlider reference (this script handles it).
/// 5. Set the panel inactive by default.
///
/// Precision aim button setup:
/// - Create a UI Button (GameObject > UI > Button).
/// - Add an Image component to it (Button already has one by default).
/// - Assign _precisionAimButton to the Button reference here.
/// - Assign _precisionAimOnSprite and _precisionAimOffSprite (two different
///   sprites for the ON and OFF states). The script swaps the button's image
///   sprite based on GameSettings.PrecisionAimEnabled.
/// - On click, the script toggles GameSettings.PrecisionAimEnabled and updates
///   the image. No need to wire up the button's OnValueChanged in the inspector
///   — the script handles it.
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

    [Tooltip("Button that toggles precision aim mode. On click, flips GameSettings.PrecisionAimEnabled. " +
             "The button's Image sprite is swapped between _precisionAimOnSprite and _precisionAimOffSprite " +
             "based on the current state. Replace the old Toggle with a Button in the inspector.")]
    [SerializeField] private Button _precisionAimButton;

    [Tooltip("Sprite shown on the precision aim button when precision mode is ON.")]
    [SerializeField] private Sprite _precisionAimOnSprite;

    [Tooltip("Sprite shown on the precision aim button when precision mode is OFF.")]
    [SerializeField] private Sprite _precisionAimOffSprite;

    [Tooltip("CapThrower reference. If assigned, ESC is yielded to the thrower while the player is aiming (regular drag OR precision mode) so the thrower can cancel the aim instead of opening the settings menu.")]
    [SerializeField] private CapThrower _capThrower;

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
            if (_precisionAimButton != null)
            {
                // Wire up the click handler. onClick toggles GameSettings.PrecisionAimEnabled
                // and refreshes the button image. No need for the designer to wire anything in
                // the inspector — the script handles it.
                _precisionAimButton.onClick.AddListener(OnPrecisionAimButtonClicked);
                // Set the initial sprite to match the current state.
                UpdatePrecisionAimButtonImage();
            }
        }
    }

    void Update()
    {
        if (Keyboard.current == null) return;

        // Yield ESC to CapThrower while the player is aiming (regular drag OR
        // precision mode). The thrower's UpdateAiming/UpdatePrecisionAiming
        // handles ESC to cancel the aim. Without this guard, ESC during aim
        // would BOTH cancel the throw AND open the settings menu.
        if (_capThrower != null && _capThrower.IsAiming)
            return;

        if (Keyboard.current[_toggleKey].wasPressedThisFrame)
        {
            TogglePause();
            UIButtonSound.PlayClick();
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

    /// <summary>
    /// Called when the precision aim button is clicked. Toggles GameSettings.PrecisionAimEnabled
    /// and refreshes the button image. The Q hotkey in CapThrower also calls SyncPrecisionAimToggle
    /// which refreshes the image — both paths go through UpdatePrecisionAimButtonImage.
    /// </summary>
    void OnPrecisionAimButtonClicked()
    {
        if (GameSettings.Instance == null) return;
        bool newValue = !GameSettings.Instance.PrecisionAimEnabled;
        GameSettings.Instance.SetPrecisionAimEnabled(newValue);
        // Play the precision toggle sound (different from standard UI click).
        UIButtonSound.PlayPrecision();
        UpdatePrecisionAimButtonImage();
    }

    /// <summary>
    /// Sync the precision-aim button's image to match the current GameSettings value.
    /// Called by CapThrower when the Q hotkey toggles precision mode, so the
    /// button visually reflects the new state without the player having to open
    /// the pause menu. Also called on Start and after a button click.
    /// </summary>
    public void SyncPrecisionAimToggle(bool value)
    {
        // `value` is already the new GameSettings state (set by CapThrower before calling).
        // Just refresh the image — GameSettings is the source of truth.
        UpdatePrecisionAimButtonImage();
    }

    /// <summary>
    /// Swaps the precision aim button's image sprite based on
    /// GameSettings.PrecisionAimEnabled. No-op if the button or sprites are missing.
    /// </summary>
    void UpdatePrecisionAimButtonImage()
    {
        if (_precisionAimButton == null) return;
        // Button doesn't expose its Image directly — fetch it via GetComponent.
        // Caching would be faster, but this runs only on click / Q press / Start,
        // so the cost is negligible and avoids a null-ref if the Image is added
        // after Awake.
        Image buttonImage = _precisionAimButton.GetComponent<Image>();
        if (buttonImage == null) return;

        bool isOn = GameSettings.Instance != null && GameSettings.Instance.PrecisionAimEnabled;
        Sprite targetSprite = isOn ? _precisionAimOnSprite : _precisionAimOffSprite;
        if (targetSprite != null && buttonImage.sprite != targetSprite)
            buttonImage.sprite = targetSprite;
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
