using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Static helper for playing UI button click sounds. All UI buttons play the
/// same sound (_clickClip) EXCEPT the precision aim toggle which plays a
/// different sound (_precisionToggleClip).
///
/// The sound clips are assigned on a UIButtonSoundSetup component placed in
/// the scene (or on the AudioManager). Any script can call:
///   UIButtonSound.PlayClick();       — plays the standard click sound.
///   UIButtonSound.PlayPrecision();   — plays the precision toggle sound.
///
/// Also provides UIButtonSound.WireButton(Button) to auto-add a click listener
/// that plays the standard sound. Call this on any button that doesn't already
/// have a click sound wired.
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
    /// replace existing listeners — just adds the sound on top. Call this on
    /// any button to get a click sound without modifying the button's handler.
    /// </summary>
    public static void WireButton(Button button)
    {
        if (button == null) return;
        button.onClick.AddListener(PlayClick);
    }
}

/// <summary>
/// Place on a GameObject in each scene that has UI buttons. Assigns the click
/// + precision sound clips to UIButtonSound on Awake. Optionally auto-wires
/// ALL buttons in the scene to play the click sound.
/// </summary>
public class UIButtonSoundSetup : MonoBehaviour
{
    [Header("Sound clips")]
    [Tooltip("Standard UI click sound played on all button presses.")]
    [SerializeField] private AudioClip _clickClip;

    [Tooltip("Sound played when the precision aim toggle is pressed (different from standard click).")]
    [SerializeField] private AudioClip _precisionToggleClip;

    [Tooltip("Volume (0-1) for UI click sounds.")]
    [Range(0f, 1f)] [SerializeField] private float _volume = 0.8f;

    [Header("Auto-wire")]
    [Tooltip("If true, finds ALL buttons in the scene on Start and wires them to play the click sound. " +
             "Buttons that already have onClick listeners will ALSO play the sound (additive). " +
             "Disable if you want to wire buttons manually via UIButtonSound.WireButton.")]
    [SerializeField] private bool _autoWireAllButtons = true;

    void Awake()
    {
        UIButtonSound.SetClips(_clickClip, _precisionToggleClip, _volume);
    }

    void Start()
    {
        if (_autoWireAllButtons)
        {
            Button[] allButtons = FindObjectsByType<Button>(FindObjectsSortMode.None);
            for (int i = 0; i < allButtons.Length; i++)
            {
                UIButtonSound.WireButton(allButtons[i]);
            }
        }
    }
}
