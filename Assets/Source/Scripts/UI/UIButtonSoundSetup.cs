using UnityEngine;
using UnityEngine.UI;

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

        // Auto-wire in Awake (not Start) so other components' Start() methods
        // can remove the click listener from specific buttons that should play
        // a different sound (e.g., the precision aim button plays
        // PlayPrecision instead of PlayClick). Unity guarantees all Awake
        // calls complete before any Start calls, so this is safe.
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
