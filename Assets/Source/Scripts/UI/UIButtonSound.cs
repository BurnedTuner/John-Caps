using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Static helper for playing UI button click sounds. All UI buttons play the
/// same sound (clickClip) EXCEPT the precision aim toggle which plays a
/// different sound (precisionToggleClip).
///
/// Any script can call:
///   UIButtonSound.PlayClick();       — plays the standard click sound.
///   UIButtonSound.PlayPrecision();   — plays the precision toggle sound.
/// </summary>
public static class UIButtonSound
{
    static AudioClip _clickClip;
    static AudioClip _precisionToggleClip;
    static float _volume = 1f;

    /// <summary>Sets the sound clips. Called by UIButtonSoundSetup on Awake.</summary>
    public static void SetClips(AudioClip click, AudioClip precision, float volume)
    {
        _clickClip = click;
        _precisionToggleClip = precision;
        _volume = Mathf.Clamp01(volume);
    }

    /// <summary>Plays the standard UI click sound.</summary>
    public static void PlayClick()
    {
        if (AudioManager.Instance != null && _clickClip != null)
            AudioManager.Instance.Play2D(_clickClip, pitch: 1f, volume: _volume);
    }

    /// <summary>Plays the precision toggle sound (different from standard click).</summary>
    public static void PlayPrecision()
    {
        if (AudioManager.Instance != null && _precisionToggleClip != null)
            AudioManager.Instance.Play2D(_precisionToggleClip, pitch: 1f, volume: _volume);
    }

    /// <summary>
    /// Wires a Button's onClick to play the standard click sound. Does NOT
    /// replace existing listeners — just adds the sound on top.
    /// </summary>
    public static void WireButton(Button button)
    {
        if (button == null) return;
        button.onClick.AddListener(PlayClick);
    }
}
