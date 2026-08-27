using UnityEngine;
using TMPro;
using UnityEngine.UI;

/// <summary>
/// Displays the kill count for both player and opponent. Binds to
/// TurnController.KillCountChanged to update the UI whenever a cap is
/// knocked off the field.
///
/// Player kills = enemy caps knocked off by the player.
/// Opponent kills = enemy caps knocked off by the opponent.
/// They are the same metric from each side's perspective:
///   player kills = opponent losses, opponent kills = player losses.
///
/// Two display modes (both can be used simultaneously):
///
/// 1. TEXT (legacy): optional TMP_Text fields showing "kills / target" or
///    just "kills" if no target is set.
///
/// 2. KILL-SPRITE IMAGE (new): ONE Image per side. The sprite on that Image
///    swaps based on the kill count: 0 kills = empty (image hidden or no
///    sprite), 1 kill = _playerKillSprites[0] / _opponentKillSprites[0] is
///    shown, 2 kills = [1], etc. Matches the user spec: "the kill count IS A
///    SINGLE IMAGE, one for player kill count, one for enemy kill count, if a
///    side has no kills the image is empty, if a side has 1 kill the image has
///    a sprite corresponding to 1 kill and so on". The sprites arrays index
///    by kills-1, so the designer assigns one sprite per kill level (5 sprites
///    for a 5-kill target). Kills beyond the array length saturate at the last
///    sprite.
///
///    SEPARATE SPRITES PER SIDE: _playerKillSprites and _opponentKillSprites
///    are independent arrays. This lets the two sides have different visual
///    indicators (e.g., player kills shown as blue pips, enemy kills shown as
///    red pips). If a side's array is null/empty, that side's Image falls back
///    to the shared _killSprites array (backward compatibility) or is hidden.
///
/// Setup:
/// 1. Place this component on a UI GameObject.
/// 2. Assign the TurnController reference (or let it auto-find).
/// 3. (Optional) Assign TMP_Text fields for the numeric readout.
/// 4. (Optional) Assign the player and opponent kill Images, and the
///    _playerKillSprites / _opponentKillSprites arrays (one sprite per kill
///    level — index 0 = 1 kill, index 1 = 2 kills, ...). If you only assign
///    _killSprites (legacy), both sides use it as a fallback.
/// 5. (Optional) Assign Sliders for the old progress-bar style.
/// </summary>
public class KillCountUI : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private TurnController _turnController;
    [SerializeField] private GameManager _gameManager;

    [Header("Text Fields (optional)")]
    [Tooltip("Text for the player's kill count. Format: 'kills / target' or just 'kills' if no target.")]
    [SerializeField] private TMP_Text _playerKillsText;

    [Tooltip("Text for the opponent's kill count. Format: 'kills / target' or just 'kills' if no target.")]
    [SerializeField] private TMP_Text _opponentKillsText;

    [Header("Kill Sprite Image (new — per-side indicator)")]
    [Tooltip("The single Image that displays the PLAYER's kill count. Its sprite is swapped " +
             "based on the kill count: 0 kills = image hidden (no sprite), 1 kill = _playerKillSprites[0], " +
             "2 kills = _playerKillSprites[1], etc. Assign one Image here.")]
    [SerializeField] private Image _playerKillImage;

    [Tooltip("The single Image that displays the OPPONENT's kill count. Same semantics as _playerKillImage.")]
    [SerializeField] private Image _opponentKillImage;

    [Tooltip("Sprites for the PLAYER's kill count. Index 0 = 1 kill, index 1 = 2 kills, etc. " +
             "Size this to match the kill target on TurnController. Separate from the opponent's " +
             "sprites so the two sides can have different visual indicators.")]
    [SerializeField] private Sprite[] _playerKillSprites;

    [Tooltip("Sprites for the OPPONENT's kill count. Index 0 = 1 kill, index 1 = 2 kills, etc. " +
             "Separate from the player's sprites so the two sides can have different visual indicators.")]
    [SerializeField] private Sprite[] _opponentKillSprites;

    [Tooltip("Shared fallback sprites (legacy). Used when _playerKillSprites or _opponentKillSprites " +
             "is null/empty. Index 0 = 1 kill, index 1 = 2 kills, etc. If you assign the per-side " +
             "arrays above, this is ignored.")]
    [SerializeField] private Sprite[] _killSprites;

    [Header("Sliders (optional, legacy)")]
    [Tooltip("Optional: slider showing player kills / target.")]
    [SerializeField] private Slider _playerKillSlider;

    [Tooltip("Optional: slider showing opponent kills / target.")]
    [SerializeField] private Slider _opponentKillSlider;

    void OnEnable()
    {
        ResolveReferences();
        Subscribe();
    }

    void OnDisable()
    {
        Unsubscribe();
    }

    void Start()
    {
        ResolveReferences();
        Subscribe();
        UpdateUI(0, 0);
    }

    void ResolveReferences()
    {
        if (_turnController == null)
            _turnController = FindFirstObjectByType<TurnController>();
        if (_gameManager == null)
            _gameManager = FindFirstObjectByType<GameManager>();
    }

    void Subscribe()
    {
        if (_turnController != null)
        {
            _turnController.KillCountChanged -= OnKillCountChanged;
            _turnController.KillCountChanged += OnKillCountChanged;
        }
        if (_gameManager != null)
        {
            _gameManager.OnBoardReset -= OnBoardReset;
            _gameManager.OnBoardReset += OnBoardReset;
        }
    }

    void Unsubscribe()
    {
        if (_turnController != null)
            _turnController.KillCountChanged -= OnKillCountChanged;
        if (_gameManager != null)
            _gameManager.OnBoardReset -= OnBoardReset;
    }

    void OnKillCountChanged(CapOwner killedOwner, int playerKills, int opponentKills)
    {
        UpdateUI(playerKills, opponentKills);
    }

    void OnBoardReset(GameManager _)
    {
        // Board reset zeroes the kill counts — refresh the UI to show 0/0.
        UpdateUI(0, 0);
    }

    void UpdateUI(int playerKills, int opponentKills)
    {
        if (_turnController == null) return;

        int target = _turnController.KillTarget;

        // --- Player kills text ---
        if (_playerKillsText != null)
        {
            _playerKillsText.text = target > 0
                ? $"{playerKills} / {target}"
                : $"{playerKills}";
        }

        // --- Opponent kills text ---
        if (_opponentKillsText != null)
        {
            _opponentKillsText.text = target > 0
                ? $"{opponentKills} / {target}"
                : $"{opponentKills}";
        }

        // --- Kill sprite Images ---
        // One Image per side. Sprite swaps based on kill count:
        //   0 kills → hide the Image (no sprite to show).
        //   K kills (K >= 1) → show _playerKillSprites[K-1] / _opponentKillSprites[K-1].
        //   K > array.Length → saturate at the last sprite.
        // Each side uses its OWN sprites array. If a side's array is null/empty,
        // falls back to the shared _killSprites array (legacy).
        UpdateKillImage(_playerKillImage, playerKills, _playerKillSprites);
        UpdateKillImage(_opponentKillImage, opponentKills, _opponentKillSprites);

        // --- Sliders ---
        if (_playerKillSlider != null)
        {
            if (target > 0)
            {
                _playerKillSlider.gameObject.SetActive(true);
                _playerKillSlider.maxValue = target;
                _playerKillSlider.SetValueWithoutNotify(Mathf.Min(playerKills, target));
            }
            else
            {
                _playerKillSlider.gameObject.SetActive(false);
            }
        }

        if (_opponentKillSlider != null)
        {
            if (target > 0)
            {
                _opponentKillSlider.gameObject.SetActive(true);
                _opponentKillSlider.maxValue = target;
                _opponentKillSlider.SetValueWithoutNotify(Mathf.Min(opponentKills, target));
            }
            else
            {
                _opponentKillSlider.gameObject.SetActive(false);
            }
        }
    }

    /// <summary>
    /// Sets the sprite on a single kill-count Image based on the kill count.
    /// 0 kills → the Image is hidden (SetActive(false) on its GameObject) so
    /// nothing is displayed, matching the user spec "if a side has no kills the
    /// image is empty". 1+ kills → the Image is shown with sprites[K-1].
    /// Kills beyond the array length saturate at the last sprite. No-op if the
    /// Image is null.
    ///
    /// Uses the given `sprites` array (per-side). If it's null/empty, falls back
    /// to the shared _killSprites array (legacy backward compatibility). If
    /// BOTH are null/empty, the Image is hidden.
    ///
    /// The Image's color is NOT touched — the designer sets per-side colors
    /// (e.g., player = blue, enemy = red) directly on the Image component in
    /// the inspector, and this script preserves them.
    /// </summary>
    void UpdateKillImage(Image image, int kills, Sprite[] sprites)
    {
        if (image == null) return;

        // Use the per-side sprites array. Fall back to the shared _killSprites
        // if the per-side array is null/empty (legacy backward compatibility).
        Sprite[] effectiveSprites = (sprites != null && sprites.Length > 0) ? sprites : _killSprites;

        if (kills <= 0 || effectiveSprites == null || effectiveSprites.Length == 0)
        {
            // No kills to show — hide the Image entirely.
            if (image.gameObject.activeSelf)
                image.gameObject.SetActive(false);
            return;
        }

        // Ensure the Image is visible.
        if (!image.gameObject.activeSelf)
            image.gameObject.SetActive(true);

        // Index into the sprites array: 1 kill → [0], 2 kills → [1], etc.
        // Clamp to the last sprite if kills exceed the array length.
        int spriteIndex = Mathf.Min(kills - 1, effectiveSprites.Length - 1);
        Sprite target = effectiveSprites[spriteIndex];
        if (target != null && image.sprite != target)
            image.sprite = target;
        // NOTE: do NOT write image.color — the designer sets per-side colors
        // directly on the Image component in the inspector.
    }
}
