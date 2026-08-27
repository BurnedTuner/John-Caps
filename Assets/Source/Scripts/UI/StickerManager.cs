using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

/// <summary>
/// Manages sticker display for cap abilities.
///
/// Stickers are placed at points on the cap's radius circle (world XZ plane
/// at the cap's Y), starting from the point farthest from the camera's forward
/// (visually "at the top" on screen), then counter-clockwise around the cap.
/// </summary>
public class StickerManager : MonoBehaviour
{
    [Header("Prefabs")]
    [SerializeField] private GameObject _stickerPanelPrefab;
    [SerializeField] private GameObject _stickerImagePrefab;
    [SerializeField] private GameObject _tooltipPrefab;

    [Header("References")]
    [Tooltip("The Canvas that stickers and tooltips render on. Assign in inspector.")]
    [SerializeField] private Canvas _uiCanvas;
    [SerializeField] private Camera _playerCamera;
    [SerializeField] private Camera _handCamera;
    [SerializeField] private CapThrower _capThrower;
    [SerializeField] private CapHand _capHand;
    [SerializeField] private GameManager _gameManager;

    [Header("Layout")]
    [Tooltip("Reference distance for perspective scaling. 0 = disabled.")]
    [SerializeField] private float _referenceDistance = 10f;

    [Tooltip("Degrees between stickers around the cap's edge. First sticker at 0\u00b0 (top).")]
    [SerializeField] private float _stickerAngleStep = 45f;

    [Tooltip("Screen-space radius (pixels) for sticker hover detection.")]
    [SerializeField] private float _stickerHoverRadius = 40f;

    [Tooltip("Screen-space offset of the hint tooltip relative to the hovered sticker. " +
             "If this offset puts the tooltip offscreen, the negated offset is tried instead.")]
    [SerializeField] private Vector2 _hintOffset = new Vector2(0f, 100f);


    [Tooltip("Scale multiplier for stickers on hand caps.")]
    [SerializeField] private float _handStickerScale = 1f;

    [Tooltip("Scale multiplier for stickers on field caps.")]
    [SerializeField] private float _fieldStickerScale = 0.7f;

    [Tooltip("Multiplier on the cap's radius for sticker placement. 1.0 = stickers sit " +
             "exactly on the cap's edge (current behavior). 0.0 = stickers sit at the cap's " +
             "center. 0.5 = halfway between center and edge. Useful for pulling stickers " +
             "inward so they don't overlap with neighboring caps or extend past the visual rim.")]
    [Range(0f, 2f)] [SerializeField] private float _stickerRadiusMultiplier = 1f;

    private readonly Dictionary<Cap, StickerPanelData> _panelPool = new();
    private readonly List<ICapAbility> _cachedAbilities = new();
    private readonly HashSet<Cap> _visibleThisFrame = new();
    // Snapshot of panelPool keys to avoid modifying during enumeration.
    private readonly List<Cap> _panelKeysSnapshot = new();

    private GameObject _tooltipInstance;
    private HintView _hintView;
    private RectTransform _tooltipRect;

    private Cap _hoveredCap;
    private bool _isHoveringSticker;

    struct StickerPanelData
    {
        public GameObject Panel;
        public List<Vector2> StickerScreenPositions;
        public float StickerVisualScale;
    }

    void Awake()
    {
        if (_playerCamera == null) _playerCamera = Camera.main;
        if (_capThrower == null) _capThrower = FindFirstObjectByType<CapThrower>();
        if (_capHand == null) _capHand = FindFirstObjectByType<CapHand>();
        if (_gameManager == null) _gameManager = FindFirstObjectByType<GameManager>();
    }

    void OnEnable()
    {
        if (_gameManager != null)
            _gameManager.OnBoardReset += HandleBoardReset;
    }

    void OnDisable()
    {
        if (_gameManager != null)
            _gameManager.OnBoardReset -= HandleBoardReset;
        ClearAllPanels();
        HideTooltip();
    }

    void Start()
    {
        if (_uiCanvas == null)
            _uiCanvas = GetComponentInParent<Canvas>();
        if (_uiCanvas == null)
            _uiCanvas = FindFirstObjectByType<Canvas>();

        if (_tooltipPrefab != null)
        {
            // Instantiate directly on the canvas — independent of StickerManager.
            Transform canvasTransform = _uiCanvas != null ? _uiCanvas.transform : transform;
            _tooltipInstance = Instantiate(_tooltipPrefab, canvasTransform);
            _tooltipInstance.SetActive(false);
            _hintView = _tooltipInstance.GetComponentInChildren<HintView>(true);
            if (_hintView == null)
                _hintView = _tooltipInstance.AddComponent<HintView>();
            _tooltipRect = _tooltipInstance.transform as RectTransform;
        }
    }

    void HandleBoardReset(GameManager _)
    {
        ClearAllPanels();
        HideTooltip();
        _hoveredCap = null;
        _isHoveringSticker = false;
    }

    void LateUpdate()
    {
        bool capIsHeld = _capThrower != null && _capThrower.CurrentState == CapThrower.State.Aiming;

        // When ANY UI panel is open (deck, settings, pause), block hover
        // detection + tooltips entirely. Stickers stay visible but don't
        // react to the cursor — no outline boost, no tooltip pop-ups.
        bool anyPanelOpen = UIBlockState.IsAnyPanelOpen;

        _visibleThisFrame.Clear();

        // Always show ALL hand cap stickers.
        if (_capHand != null)
        {
            for (int i = 0; i < _capHand.HandSize; i++)
            {
                Cap cap = _capHand.GetHandCap(i);
                if (cap != null) _visibleThisFrame.Add(cap);
            }
        }

        // Always show ALL field cap stickers — regardless of aiming state.
        // Stickers stay visible during aiming (they don't obstruct the aiming
        // process because their Images have raycastTarget = false, so clicks
        // pass through to the field/hand caps behind them).
        IReadOnlyList<Cap> allCaps = CapRegistry.AllCaps;
        for (int i = 0; i < allCaps.Count; i++)
        {
            Cap cap = allCaps[i];
            if (cap == null || cap.HasLeftGame || cap.IsParked) continue;
            _visibleThisFrame.Add(cap);
        }

        if (capIsHeld || anyPanelOpen)
        {
            // While aiming OR any UI panel open: skip hover detection entirely.
            // No outline boost, no tooltips. Stickers stay visible (added above)
            // but don't react to the cursor. Clear any stale hover state.
            _hoveredCap = null;
            _isHoveringSticker = false;

            // Clear hover outline on all caps (field + hand).
            IReadOnlyList<Cap> registryCaps = CapRegistry.AllCaps;
            for (int i = 0; i < registryCaps.Count; i++)
            {
                Cap cap = registryCaps[i];
                if (cap == null || cap.HasLeftGame) continue;
                cap.SetHovered(false);
            }
            if (_capHand != null)
            {
                for (int i = 0; i < _capHand.HandSize; i++)
                {
                    Cap cap = _capHand.GetHandCap(i);
                    if (cap == null || cap.HasLeftGame) continue;
                    cap.SetHovered(false);
                }
            }

            HideTooltip();
        }
        else
        {
            // Not aiming: run hover detection for outline + tooltip.
            if (DeckPanelUI.IsCursorOverPanel)
            {
                _hoveredCap = null;
            }
            else
            {
                Cap hoveredHand = GetHoveredHandCap();
                if (hoveredHand != null)
                {
                    _hoveredCap = hoveredHand;
                }
                else
                {
                    Cap hovered = GetHoveredFieldCap();
                    if (hovered != null)
                    {
                        _hoveredCap = hovered;
                    }
                    else
                    {
                        _hoveredCap = null;
                    }
                }
            }

            // Update hover outline state on ALL caps (field + hand).
            if (_hoveredCap != null && !_hoveredCap.HasLeftGame)
                _hoveredCap.SetHovered(true);

            // Clear hover on all previously hovered caps (except the current one).
            IReadOnlyList<Cap> registryCaps2 = CapRegistry.AllCaps;
            for (int i = 0; i < registryCaps2.Count; i++)
            {
                Cap cap = registryCaps2[i];
                if (cap == null || cap == _hoveredCap || cap.HasLeftGame) continue;
                cap.SetHovered(false);
            }
            if (_capHand != null)
            {
                for (int i = 0; i < _capHand.HandSize; i++)
                {
                    Cap cap = _capHand.GetHandCap(i);
                    if (cap == null || cap == _hoveredCap || cap.HasLeftGame) continue;
                    cap.SetHovered(false);
                }
            }
        }

        foreach (Cap cap in _visibleThisFrame)
            EnsurePanelForCap(cap);

        // Hide panels not visible this frame.
        foreach (var kvp in _panelPool)
        {
            if (kvp.Key == null) continue;
            bool shouldShow = _visibleThisFrame.Contains(kvp.Key);
            if (kvp.Value.Panel.activeSelf != shouldShow)
                kvp.Value.Panel.SetActive(shouldShow);
        }

        UpdatePanelPositions();

        // Only run sticker hover (tooltip) detection when NOT aiming AND no panel open.
        if (!capIsHeld && !anyPanelOpen)
            HandleStickerHover();
        else
            HideTooltip();
    }

    void EnsurePanelForCap(Cap cap)
    {
        if (cap == null) return;

        _cachedAbilities.Clear();
        var abilities = cap.GetComponents<ICapAbility>();
        for (int i = 0; i < abilities.Length; i++)
        {
            if (abilities[i] != null)
                _cachedAbilities.Add(abilities[i]);
        }

        if (_cachedAbilities.Count == 0) return;
        if (_stickerPanelPrefab == null || _stickerImagePrefab == null) return;

        if (!_panelPool.TryGetValue(cap, out StickerPanelData data) || data.Panel == null)
        {
            data.Panel = Instantiate(_stickerPanelPrefab, transform);
            data.StickerScreenPositions = new List<Vector2>();

            // Ensure the panel itself doesn't intercept raycasts. The panel
            // is a container for sticker Images — it should be invisible to
            // the raycast system so clicks pass through to the caps behind it.
            Image panelImg = data.Panel.GetComponent<Image>();
            if (panelImg != null) panelImg.raycastTarget = false;
            // Also disable any CanvasGroup's blocksRaycasts on the panel (if it
            // has one) so it doesn't block input.
            CanvasGroup cg = data.Panel.GetComponent<CanvasGroup>();
            if (cg != null) cg.blocksRaycasts = false;
        }

        GameObject panel = data.Panel;
        int currentCount = panel.transform.childCount;
        int neededCount = 0;
        for (int i = 0; i < _cachedAbilities.Count; i++)
            if (_cachedAbilities[i].StickerSprite != null) neededCount++;

        if (currentCount != neededCount)
        {
            for (int c = panel.transform.childCount - 1; c >= 0; c--)
                Destroy(panel.transform.GetChild(c).gameObject);

            for (int i = 0; i < _cachedAbilities.Count; i++)
            {
                if (_cachedAbilities[i].StickerSprite == null) continue;
                GameObject stickerObj = Instantiate(_stickerImagePrefab, panel.transform);
                Image img = stickerObj.GetComponent<Image>();
                if (img == null) img = stickerObj.AddComponent<Image>();
                img.sprite = _cachedAbilities[i].StickerSprite;
                img.preserveAspect = true;
                // Stickers must NOT intercept raycasts — otherwise they'd block
                // hover detection on the field/hand caps behind them. The
                // sticker hover detection is done manually in HandleStickerHover
                // (screen-space distance), not via Unity's raycast system.
                img.raycastTarget = false;
                RectTransform srt = stickerObj.transform as RectTransform;
                if (srt != null) srt.sizeDelta = new Vector2(64f, 64f);
                stickerObj.SetActive(true);

                // Set level badge via StickerView component.
                StickerView view = stickerObj.GetComponent<StickerView>();
                if (view == null)
                    view = stickerObj.AddComponent<StickerView>();
                view.SetLevel(_cachedAbilities[i].Level);
            }
        }

        data.StickerScreenPositions = new List<Vector2>(neededCount);
        for (int i = 0; i < neededCount; i++)
            data.StickerScreenPositions.Add(Vector2.zero);

        _panelPool[cap] = data;
        panel.SetActive(true);
    }

    void UpdatePanelPositions()
    {
        // Snapshot keys to avoid modifying the dictionary during iteration.
        _panelKeysSnapshot.Clear();
        foreach (var key in _panelPool.Keys)
            _panelKeysSnapshot.Add(key);

        for (int k = 0; k < _panelKeysSnapshot.Count; k++)
        {
            Cap cap = _panelKeysSnapshot[k];
            if (cap == null) continue;
            if (!_panelPool.TryGetValue(cap, out StickerPanelData data)) continue;
            if (data.Panel == null || !data.Panel.activeSelf) continue;

            bool isHandCap = !CapRegistry.Contains(cap);
            Camera cam = (isHandCap && _handCamera != null) ? _handCamera : _playerCamera;
            if (cam == null) continue;

            Vector3 capCenter = cap.transform.position;
            // Stickers sit on a circle of this radius around the cap's center.
            // The multiplier lets the designer pull stickers inward (e.g., 0.5
            // places them halfway between center and edge) or push them outward
            // (values > 1). 1.0 = exactly on the cap's edge (legacy behavior).
            float worldRadius = (cap.Parameters != null ? cap.Parameters.Radius : 0.5f)
                * Mathf.Max(0f, _stickerRadiusMultiplier);

            Vector3 screenCenter = cam.WorldToScreenPoint(capCenter);
            if (screenCenter.z < 0)
            {
                data.Panel.SetActive(false);
                _panelPool[cap] = data;
                continue;
            }

            // "Up" direction on the XZ plane (appears "up" on screen from camera's POV).
            Vector3 camUpFlat = Vector3.ProjectOnPlane(cam.transform.up, Vector3.up);
            if (camUpFlat.sqrMagnitude < 0.0001f)
                camUpFlat = Vector3.forward;
            else
                camUpFlat.Normalize();

            // "Right" direction on the XZ plane.
            Vector3 camRightFlat = Vector3.Cross(Vector3.up, camUpFlat).normalized;

            // Perspective scale.
            float dist = Vector3.Distance(capCenter, cam.transform.position);
            float perspectiveScale = _referenceDistance > 0f
                ? Mathf.Clamp(_referenceDistance / Mathf.Max(0.1f, dist), 0.1f, 5f)
                : 1f;
            float baseScale = isHandCap ? _handStickerScale : _fieldStickerScale;
            float finalScale = baseScale * perspectiveScale;

            RectTransform panelRT = data.Panel.transform as RectTransform;
            RectTransform parentRT = panelRT != null ? panelRT.parent as RectTransform : null;
            if (parentRT == null) continue;

            // For Screen Space - Overlay, uiCamera is null (correct).
            // For Screen Space - Camera, uiCamera is the canvas worldCamera.
            Camera uiCamera = (_uiCanvas != null && _uiCanvas.renderMode == RenderMode.ScreenSpaceCamera)
                ? _uiCanvas.worldCamera
                : null;

            // Position the panel at the cap's screen center.
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                parentRT, screenCenter, uiCamera, out Vector2 panelLocalPos);
            panelRT.anchoredPosition = panelLocalPos;
            panelRT.localScale = Vector3.one;

            int stickerCount = panelRT.childCount;
            // Ensure the screen positions list matches the sticker count.
            while (data.StickerScreenPositions.Count < stickerCount)
                data.StickerScreenPositions.Add(Vector2.zero);
            while (data.StickerScreenPositions.Count > stickerCount)
                data.StickerScreenPositions.RemoveAt(data.StickerScreenPositions.Count - 1);

            for (int i = 0; i < stickerCount; i++)
            {
                RectTransform stickerRT = panelRT.GetChild(i) as RectTransform;
                if (stickerRT == null) continue;

                // Angle: first sticker at 0° (camUpFlat = "top"), then counter-clockwise.
                float angleDeg = i * _stickerAngleStep;
                float angleRad = angleDeg * Mathf.Deg2Rad;

                // Direction on the XZ plane: counter-clockwise from camUpFlat.
                Vector3 dir = (camUpFlat * Mathf.Cos(angleRad) - camRightFlat * Mathf.Sin(angleRad)).normalized;

                // World position ON the cap's radius circle (exactly on the edge).
                Vector3 stickerWorldPos = capCenter + dir * worldRadius;

                // Project to screen.
                Vector3 stickerScreenPos = cam.WorldToScreenPoint(stickerWorldPos);

                // Store for hover detection.
                data.StickerScreenPositions[i] = new Vector2(stickerScreenPos.x, stickerScreenPos.y);

                // Convert to UI local position relative to the panel center.
                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    parentRT, stickerScreenPos, uiCamera, out Vector2 localPos);
                stickerRT.anchoredPosition = localPos - panelLocalPos;
                stickerRT.localScale = new Vector3(finalScale, finalScale, 1f);
            }

            // Store the visual scale for hover-radius scaling.
            data.StickerVisualScale = finalScale;

            _panelPool[cap] = data;
        }
    }

    void HandleStickerHover()
    {
        if (_tooltipInstance == null || Mouse.current == null) return;

        Vector2 mousePos = Mouse.current.position.ReadValue();
        _isHoveringSticker = false;

        foreach (Cap cap in _visibleThisFrame)
        {
            if (cap == null) continue;
            if (!_panelPool.TryGetValue(cap, out StickerPanelData data)) continue;
            if (data.Panel == null || !data.Panel.activeSelf) continue;
            if (data.StickerScreenPositions == null) continue;

            for (int i = 0; i < data.StickerScreenPositions.Count; i++)
            {
                float d = Vector2.Distance(mousePos, data.StickerScreenPositions[i]);

                // Scale hover radius by the sticker's visual scale so smaller
                // stickers have smaller hover areas.
                float hoverRadius = _stickerHoverRadius * data.StickerVisualScale;
                if (d <= hoverRadius)
                {
                    _isHoveringSticker = true;
                    _hoveredCap = cap;

                    var abilities = cap.GetComponents<ICapAbility>();
                    int abilityIndex = 0;
                    int stickerIdx = 0;
                    for (int a = 0; a < abilities.Length; a++)
                    {
                        if (abilities[a] == null || abilities[a].StickerSprite == null) continue;
                        if (stickerIdx == i) { abilityIndex = a; break; }
                        stickerIdx++;
                    }

                    if (abilityIndex < abilities.Length)
                    {
                        ICapAbility ability = abilities[abilityIndex];
                        if (ability != null)
                        {
                            ShowTooltip(ability.Description, data.StickerScreenPositions[i]);
                            return;
                        }
                    }
                }
            }
        }

        HideTooltip();
    }

    void ShowTooltip(string text, Vector2 stickerScreenPos)
    {
        if (_tooltipInstance == null || _tooltipRect == null) return;

        _tooltipInstance.SetActive(true);
        if (_hintView != null)
            _hintView.SetText(text);

        // Force layout rebuild so we have accurate dimensions.
        UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(_tooltipRect);

        Vector2 tooltipSize = _tooltipRect.rect.size * _tooltipRect.lossyScale.x;
        float sw = Screen.width;
        float sh = Screen.height;

        // Try the offset as-is. If the tooltip would go offscreen, try the negated offset.
        Vector2[] offsets = { _hintOffset, -_hintOffset };

        foreach (Vector2 offset in offsets)
        {
            Vector2 target = stickerScreenPos + offset;

            // Check if the tooltip would be fully on screen.
            bool onScreen = target.x >= 0f && target.y >= 0f &&
                            target.x + tooltipSize.x <= sw &&
                            target.y + tooltipSize.y <= sh;

            if (!onScreen) continue;

            // Convert screen pixels to the tooltip's parent local coordinates.
            RectTransform parentRT = _tooltipRect.parent as RectTransform;
            Camera uiCamera = (_uiCanvas != null && _uiCanvas.renderMode == RenderMode.ScreenSpaceCamera)
                ? _uiCanvas.worldCamera
                : null;

            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                parentRT, target, uiCamera, out Vector2 localPos);
            _tooltipRect.anchoredPosition = localPos;
            return;
        }

        // Both offsets went offscreen — just use the first one.
        {
            Vector2 target = stickerScreenPos + _hintOffset;
            RectTransform parentRT = _tooltipRect.parent as RectTransform;
            Camera uiCamera = (_uiCanvas != null && _uiCanvas.renderMode == RenderMode.ScreenSpaceCamera)
                ? _uiCanvas.worldCamera
                : null;

            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                parentRT, target, uiCamera, out Vector2 localPos);
            _tooltipRect.anchoredPosition = localPos;
        }
    }

    void HideTooltip()
    {
        if (_tooltipInstance != null)
            _tooltipInstance.SetActive(false);
    }

    Cap GetHoveredFieldCap()
    {
        if (_playerCamera == null || Mouse.current == null) return null;
        if (_capThrower != null && _capThrower.CurrentState == CapThrower.State.Aiming) return null;

        Vector2 mousePos = Mouse.current.position.ReadValue();
        Ray ray = _playerCamera.ScreenPointToRay(mousePos);

        RaycastHit[] hits = Physics.RaycastAll(ray, 100f);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        for (int i = 0; i < hits.Length; i++)
        {
            if (hits[i].collider == null) continue;
            Cap cap = hits[i].collider.GetComponentInParent<Cap>();
            if (cap == null) continue;
            if (cap.HasLeftGame) continue;
            return cap;
        }
        return null;
    }

    /// <summary>
    /// Returns the hand cap under the cursor, or null. Hand caps render on the
    /// HandCamera overlay (not the PlayerCamera), so we use CapHand's existing
    /// screen-distance test against the HandCamera. Without this, hovering a
    /// hand cap would never set _hoveredCap and the hover outline would never
    /// grow until the player hovered a sticker image (which uses a separate
    /// screen-distance test against sticker positions).
    /// </summary>
    Cap GetHoveredHandCap()
    {
        if (_capHand == null || Mouse.current == null) return null;
        if (_capThrower != null && _capThrower.CurrentState == CapThrower.State.Aiming) return null;

        Camera capCam = _handCamera != null ? _handCamera : _playerCamera;
        if (capCam == null) return null;

        Vector2 mousePos = Mouse.current.position.ReadValue();
        return _capHand.GetCapUnderScreenPosition(mousePos, capCam);
    }

    void ClearAllPanels()
    {
        foreach (var kvp in _panelPool)
        {
            if (kvp.Value.Panel != null)
                Destroy(kvp.Value.Panel);
        }
        _panelPool.Clear();
        _visibleThisFrame.Clear();
        _panelKeysSnapshot.Clear();
    }
}
