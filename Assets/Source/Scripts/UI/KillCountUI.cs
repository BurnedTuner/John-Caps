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
/// Setup:
/// 1. Place this component on a UI GameObject.
/// 2. Assign the TurnController reference (or let it auto-find).
/// 3. Assign TMP_Text fields for player kills and opponent kills.
/// </summary>
public class KillCountUI : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private TurnController _turnController;
    [SerializeField] private GameManager _gameManager;

    [Header("Text Fields")]
    [Tooltip("Text for the player's kill count. Format: 'kills / target' or just 'kills' if no target.")]
    [SerializeField] private TMP_Text _playerKillsText;

    [Tooltip("Text for the opponent's kill count. Format: 'kills / target' or just 'kills' if no target.")]
    [SerializeField] private TMP_Text _opponentKillsText;

    [Header("Sliders (optional)")]
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
}
