using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;

/// <summary>
/// Shows/hides precision aim UI elements when CapThrower enters/exits the
/// PrecisionAiming state. Elements shown:
///
/// 1. D-pad buttons (up/down/left/right) — alternative to WASD. Holding a
///    button continuously nudges the aim point using the SAME acceleration
///    curve as WASD (the input is fed into CapThrower._precisionDPadInput
///    which is combined with keyboard input).
/// 2. Confirm button — fires the throw (Spacebar or Enter).
/// 3. Cancel button — cancels the throw (ESC).
/// 4. Spacebar prompt — visual reminder.
///
/// All on-screen buttons react to their keyboard counterparts being pressed:
/// the button's image color changes when the corresponding key is held.
/// </summary>
public class PrecisionAimUI : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The CapThrower to watch for PrecisionAiming state.")]
    [SerializeField] private CapThrower _capThrower;

    [Tooltip("The panel GameObject shown/hidden when precision aim is active.")]
    [SerializeField] private GameObject _panel;

    [Header("D-pad buttons")]
    [Tooltip("Button that nudges the aim point UP (equivalent to W).")]
    [SerializeField] private Button _upButton;

    [Tooltip("Button that nudges the aim point DOWN (equivalent to S).")]
    [SerializeField] private Button _downButton;

    [Tooltip("Button that nudges the aim point LEFT (equivalent to A).")]
    [SerializeField] private Button _leftButton;

    [Tooltip("Button that nudges the aim point RIGHT (equivalent to D).")]
    [SerializeField] private Button _rightButton;

    [Header("Action buttons")]
    [Tooltip("Button that confirms the throw (equivalent to Spacebar / Enter).")]
    [SerializeField] private Button _confirmButton;

    [Tooltip("Button that cancels the throw (equivalent to ESC).")]
    [SerializeField] private Button _cancelButton;

    [Tooltip("GameObject shown as a spacebar prompt (e.g., 'SPACE — Confirm').")]
    [SerializeField] private GameObject _spacebarPrompt;

    [Header("Keyboard visual feedback")]
    [Tooltip("Color applied to buttons when their keyboard counterpart is held.")]
    [SerializeField] private Color _keyHeldColor = new Color(0.6f, 0.8f, 1f, 1f);

    private bool _wasPrecisionAiming;

    // Hold tracking for d-pad buttons. Each is true while the pointer is down
    // on that button. Set by HoldableButton callbacks.
    private bool _upHeld;
    private bool _downHeld;
    private bool _leftHeld;
    private bool _rightHeld;

    // Cached original button colors.
    private Color _upNormalColor;
    private Color _downNormalColor;
    private Color _leftNormalColor;
    private Color _rightNormalColor;
    private Color _confirmNormalColor;
    private Color _cancelNormalColor;
    private bool _colorsCached;

    void Awake()
    {
        if (_capThrower == null) _capThrower = FindFirstObjectByType<CapThrower>();
    }

    void OnEnable()
    {
        WireButtons();
    }

    void OnDisable()
    {
        UnwireButtons();
        HidePanel();
        // Clear d-pad input when disabled.
        if (_capThrower != null) _capThrower.SetDPadInput(Vector2.zero);
    }

    void Start()
    {
        HidePanel();
    }

    void WireButtons()
    {
        // Action buttons use onClick (one-shot press = action).
        if (_confirmButton != null) _confirmButton.onClick.AddListener(OnConfirmPressed);
        if (_cancelButton != null) _cancelButton.onClick.AddListener(OnCancelPressed);

        // D-pad buttons use HoldableButton for press-and-hold tracking.
        EnsureHoldable(_upButton, isDown => _upHeld = isDown);
        EnsureHoldable(_downButton, isDown => _downHeld = isDown);
        EnsureHoldable(_leftButton, isDown => _leftHeld = isDown);
        EnsureHoldable(_rightButton, isDown => _rightHeld = isDown);
    }

    void UnwireButtons()
    {
        if (_confirmButton != null) _confirmButton.onClick.RemoveListener(OnConfirmPressed);
        if (_cancelButton != null) _cancelButton.onClick.RemoveListener(OnCancelPressed);
        _upHeld = _downHeld = _leftHeld = _rightHeld = false;
    }

    static void EnsureHoldable(Button btn, System.Action<bool> onHoldChanged)
    {
        if (btn == null) return;
        var hb = btn.GetComponent<HoldableButton>();
        if (hb == null)
            hb = btn.gameObject.AddComponent<HoldableButton>();
        hb.OnHoldChanged = onHoldChanged;
    }

    void CacheColors()
    {
        if (_colorsCached) return;
        if (_upButton != null) _upNormalColor = _upButton.image.color;
        if (_downButton != null) _downNormalColor = _downButton.image.color;
        if (_leftButton != null) _leftNormalColor = _leftButton.image.color;
        if (_rightButton != null) _rightNormalColor = _rightButton.image.color;
        if (_confirmButton != null) _confirmNormalColor = _confirmButton.image.color;
        if (_cancelButton != null) _cancelNormalColor = _cancelButton.image.color;
        _colorsCached = true;
    }

    void Update()
    {
        if (_capThrower == null) return;

        bool isPrecision = _capThrower.IsPrecisionAiming;

        if (isPrecision != _wasPrecisionAiming)
        {
            _wasPrecisionAiming = isPrecision;
            if (isPrecision)
            {
                ShowPanel();
                CacheColors();
            }
            else
            {
                HidePanel();
                _upHeld = _downHeld = _leftHeld = _rightHeld = false;
                _capThrower.SetDPadInput(Vector2.zero);
            }
        }

        if (!isPrecision) return;

        var kb = Keyboard.current;

        // --- Feed d-pad button hold state into CapThrower ---
        // The d-pad input is COMBINED with keyboard WASD in CapThrower's
        // UpdatePrecisionAiming. Both use the same _precisionAccelTimer and
        // PrecisionAimAccelerationCurve — holding the on-screen button
        // produces the EXACT SAME acceleration as holding the key.
        Vector2 dpadInput = Vector2.zero;
        if (_upHeld) dpadInput.y += 1f;
        if (_downHeld) dpadInput.y -= 1f;
        if (_leftHeld) dpadInput.x -= 1f;
        if (_rightHeld) dpadInput.x += 1f;
        _capThrower.SetDPadInput(dpadInput);

        // --- Keyboard visual feedback ---
        // Set button colors based on whether key OR button is held.
        bool wHeld = kb != null && (kb.wKey.isPressed || kb.upArrowKey.isPressed);
        bool sHeld = kb != null && (kb.sKey.isPressed || kb.downArrowKey.isPressed);
        bool aHeld = kb != null && (kb.aKey.isPressed || kb.leftArrowKey.isPressed);
        bool dHeld = kb != null && (kb.dKey.isPressed || kb.rightArrowKey.isPressed);
        bool confirmHeld = kb != null && (kb.spaceKey.isPressed || kb.enterKey.isPressed);
        bool cancelHeld = kb != null && kb.escapeKey.isPressed;

        SetButtonColor(_upButton, _upHeld || wHeld, _upNormalColor);
        SetButtonColor(_downButton, _downHeld || sHeld, _downNormalColor);
        SetButtonColor(_leftButton, _leftHeld || aHeld, _leftNormalColor);
        SetButtonColor(_rightButton, _rightHeld || dHeld, _rightNormalColor);
        SetButtonColor(_confirmButton, confirmHeld, _confirmNormalColor);
        SetButtonColor(_cancelButton, cancelHeld, _cancelNormalColor);
    }

    void SetButtonColor(Button btn, bool held, Color normalColor)
    {
        if (btn == null || btn.image == null) return;
        btn.image.color = held ? _keyHeldColor : normalColor;
    }

    void ShowPanel()
    {
        if (_panel != null) _panel.SetActive(true);
    }

    void HidePanel()
    {
        if (_panel != null) _panel.SetActive(false);
    }

    void OnConfirmPressed()
    {
        if (_capThrower == null || !_capThrower.IsPrecisionAiming) return;
        _capThrower.ConfirmPrecisionThrow();
    }

    void OnCancelPressed()
    {
        if (_capThrower == null || !_capThrower.IsPrecisionAiming) return;
        _capThrower.CancelPrecisionThrow();
    }
}

/// <summary>
/// Helper component that tracks pointer down/up on a UI Button.
/// Fires OnHoldChanged(true) on pointer down, OnHoldChanged(false) on pointer up.
/// </summary>
public class HoldableButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
{
    public System.Action<bool> OnHoldChanged;

    public void OnPointerDown(PointerEventData eventData)
    {
        OnHoldChanged?.Invoke(true);
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        OnHoldChanged?.Invoke(false);
    }
}
