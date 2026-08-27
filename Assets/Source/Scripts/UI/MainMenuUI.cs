using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Main menu controller — wires the main menu's buttons:
///   - Settings button → toggles a settings panel (inactive by default).
///   - Back button → closes the settings panel.
///   - Exit button → quits the game (works in editor + build).
///
/// The settings panel should contain SFX/BGM volume sliders with a
/// SettingsBinder component on it (which binds the sliders to GameSettings
/// + saves to PlayerPrefs). This script just handles show/hide + exit.
///
/// Setup:
/// 1. Add this component to any GameObject in the main menu scene.
/// 2. Assign _settingsButton (the "Settings" toggle button).
/// 3. Assign _backButton (inside the settings panel, closes it).
/// 4. Assign _exitButton (the "Exit" / "Quit" button).
/// 5. Assign _settingsPanel (the panel GameObject — set inactive by default).
/// 6. Inside _settingsPanel, add SettingsBinder + SFX/BGM sliders.
/// </summary>
public class MainMenuUI : MonoBehaviour
{
    [Header("Buttons")]
    [Tooltip("The 'Settings' button that opens the settings panel.")]
    [SerializeField] private Button _settingsButton;

    [Tooltip("The 'Back' button inside the settings panel (closes it).")]
    [SerializeField] private Button _backButton;

    [Tooltip("The 'Exit' / 'Quit' button that closes the game.")]
    [SerializeField] private Button _exitButton;

    [Header("Panel")]
    [Tooltip("The settings panel GameObject. Should start inactive (uncheck in inspector).")]
    [SerializeField] private GameObject _settingsPanel;

    void Start()
    {
        if (_settingsButton != null)
            _settingsButton.onClick.AddListener(ToggleSettingsPanel);

        if (_backButton != null)
            _backButton.onClick.AddListener(CloseSettingsPanel);

        if (_exitButton != null)
            _exitButton.onClick.AddListener(ExitGame);

        // Ensure the settings panel starts hidden.
        if (_settingsPanel != null)
            _settingsPanel.SetActive(false);
    }

    void OnDestroy()
    {
        if (_settingsButton != null)
            _settingsButton.onClick.RemoveListener(ToggleSettingsPanel);
        if (_backButton != null)
            _backButton.onClick.RemoveListener(CloseSettingsPanel);
        if (_exitButton != null)
            _exitButton.onClick.RemoveListener(ExitGame);
    }

    /// <summary>Toggles the settings panel open/closed.</summary>
    public void ToggleSettingsPanel()
    {
        if (_settingsPanel != null)
        {
            bool newState = !_settingsPanel.activeSelf;
            _settingsPanel.SetActive(newState);
            UIBlockState.SetSettingsPanelOpen(newState);
        }
    }

    /// <summary>Closes the settings panel.</summary>
    public void CloseSettingsPanel()
    {
        if (_settingsPanel != null)
            _settingsPanel.SetActive(false);
        UIBlockState.SetSettingsPanelOpen(false);
    }

    /// <summary>
    /// Quits the game. Works in both the editor (stops play mode) and in
    /// builds (calls Application.Quit).
    /// </summary>
    public void ExitGame()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}
