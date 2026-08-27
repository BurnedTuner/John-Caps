using UnityEngine;

/// <summary>
/// Static helper that tracks whether ANY UI panel is currently open and
/// should block game-world interactions (cap hover, sticker hover, throw start).
///
/// Components register their open/closed state via SetDeckPanelOpen,
/// SetSettingsPanelOpen, SetPauseMenuOpen. CapThrower and StickerManager
/// check IsAnyPanelOpen before processing input.
///
/// Place this on any persistent GameObject (or just call the static methods —
/// no instance needed). The state is static, so it persists across scenes.
/// </summary>
public static class UIBlockState
{
    static bool _deckPanelOpen;
    static bool _settingsPanelOpen;
    static bool _pauseMenuOpen;

    /// <summary>True if the deck panel is currently open (anywhere).</summary>
    public static bool IsDeckPanelOpen => _deckPanelOpen;
    /// <summary>True if the settings panel is currently open (main menu or elsewhere).</summary>
    public static bool IsSettingsPanelOpen => _settingsPanelOpen;
    /// <summary>True if the pause menu is currently open.</summary>
    public static bool IsPauseMenuOpen => _pauseMenuOpen;

    /// <summary>True if ANY blocking panel is open. Checked by CapThrower + StickerManager.</summary>
    public static bool IsAnyPanelOpen => _deckPanelOpen || _settingsPanelOpen || _pauseMenuOpen;

    public static void SetDeckPanelOpen(bool open) => _deckPanelOpen = open;
    public static void SetSettingsPanelOpen(bool open) => _settingsPanelOpen = open;
    public static void SetPauseMenuOpen(bool open) => _pauseMenuOpen = open;

    /// <summary>Reset all panel states (e.g., on scene load).</summary>
    public static void Reset()
    {
        _deckPanelOpen = false;
        _settingsPanelOpen = false;
        _pauseMenuOpen = false;
    }
}
