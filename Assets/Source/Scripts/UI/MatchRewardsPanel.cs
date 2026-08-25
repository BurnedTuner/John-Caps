using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

/// <summary>
/// Panel shown after a battle ends. Displays the caps the player lost and
/// gained during the battle, with stickers arranged radially around each cap
/// icon — same visual language as <see cref="DeckPanelUI"/> / StickerManager.
///
/// Hover a sticker → tooltip with the ability description.
///
/// Layout: caps are arranged horizontally inside a constrained panel. If all
/// caps fit at the natural spacing, they're spaced out. If they don't fit,
/// they OVERLAP — each cap is shifted left so it partially covers the previous
/// one (the most recent cap on top). The overlap step is computed so the row
/// fits exactly within the panel width.
///
/// Self-subscribes to <see cref="RunManager.OnBattleEnded"/>. Place this on a
/// GameObject inside the battle scene's UI Canvas. Set the panel GameObject
/// inactive by default — it activates itself when a battle ends.
///
/// Setup in Unity:
/// 1. Add this component to a GameObject in the battle scene's Canvas.
/// 2. Assign _rootPanel (the GameObject to show/hide — set inactive by default).
/// 3. Assign _lostContentParent and _gainedContentParent (RectTransforms where
///    cap entries will be instantiated).
/// 4. Assign _lostSection and _gainedSection (parent GameObjects of the two
///    content areas — auto-hidden when their list is empty).
/// 5. Assign _capEntryPrefab (root has an Image — the cap icon).
/// 6. Assign _stickerImagePrefab (Image — one per ICapAbility sticker).
/// 7. Assign _tooltipPrefab (HintView — for sticker hover tooltips).
/// 8. (Optional) Assign _lostCountText / _gainedCountText for "+3 / -2" labels.
/// 9. (Optional) Assign _fallbackCapSprite for caps with no icon.
/// </summary>
public class MatchRewardsPanel : MonoBehaviour
{
    [Header("Root")]
    [Tooltip("The panel GameObject (the whole rewards UI). Set inactive by default — " +
             "this script activates it when OnBattleEnded fires.")]
    [SerializeField] private GameObject _rootPanel;

    [Header("Sections")]
    [Tooltip("Parent GameObject of the 'Lost' section. Auto-hidden when no caps were lost.")]
    [SerializeField] private GameObject _lostSection;

    [Tooltip("Parent RectTransform where lost-cap entries are instantiated.")]
    [SerializeField] private RectTransform _lostContentParent;

    [Tooltip("Parent GameObject of the 'Gained' section. Auto-hidden when no caps were gained.")]
    [SerializeField] private GameObject _gainedSection;

    [Tooltip("Parent RectTransform where gained-cap entries are instantiated.")]
    [SerializeField] private RectTransform _gainedContentParent;

    [Header("Prefabs")]
    [Tooltip("Prefab for one cap entry. Root should have an Image (the cap icon). " +
             "Stickers are positioned radially around the icon by this script — " +
             "NO StickerContainer child is needed.")]
    [SerializeField] private GameObject _capEntryPrefab;

    [Tooltip("Prefab for a sticker Image (one per ICapAbility sticker).")]
    [SerializeField] private GameObject _stickerImagePrefab;

    [Tooltip("Tooltip prefab (same as StickerManager / DeckPanelUI uses — should have a HintView).")]
    [SerializeField] private GameObject _tooltipPrefab;

    [Header("Cap entry layout")]
    [Tooltip("Size (pixels) of each cap entry. The cap icon should fill this size " +
             "(set its RectTransform to stretch to the entry, or set the icon size " +
             "explicitly). The overlap layout uses this size to compute spacing.")]
    [SerializeField] private Vector2 _capEntrySize = new Vector2(80f, 80f);

    [Tooltip("Gap (pixels) between adjacent cap entries when they all fit naturally. " +
             "If they don't fit, the gap shrinks to 0 and then goes negative (overlap).")]
    [SerializeField] private float _capSpacing = 12f;

    [Tooltip("Vertical centering offset. Entries are centered vertically in the " +
             "content parent unless this is non-zero (then they're offset by this much).")]
    [SerializeField] private float _verticalOffset = 0f;

    [Header("Sticker layout")]
    [Tooltip("Degrees between stickers around the cap icon. First sticker at 0° (top), " +
             "then counter-clockwise. Mirrors StickerManager._stickerAngleStep.")]
    [SerializeField] private float _stickerAngleStep = 45f;

    [Tooltip("Radius (pixels) of the circle on which stickers sit around the cap icon's center.")]
    [SerializeField] private float _stickerRadiusPixels = 48f;

    [Tooltip("Size (pixels) of each sticker Image in the rewards panel.")]
    [Min(1f)] [SerializeField] private Vector2 _stickerSize = new Vector2(32f, 32f);

    [Tooltip("Screen-space radius (pixels) for sticker hover detection. Scaled by the " +
             "sticker's visual size — a 64x64 sticker uses 2x this radius, a 32x32 sticker " +
             "uses the base radius.")]
    [SerializeField] private float _stickerHoverRadius = 32f;

    [Tooltip("Screen-space offset of the hint tooltip relative to the hovered sticker.")]
    [SerializeField] private Vector2 _hintOffset = new Vector2(0f, 60f);

    [Header("Optional")]
    [Tooltip("Optional text showing the lost count (e.g., '-2').")]
    [SerializeField] private TMPro.TMP_Text _lostCountText;

    [Tooltip("Optional text showing the gained count (e.g., '+3').")]
    [SerializeField] private TMPro.TMP_Text _gainedCountText;

    [Tooltip("Fallback sprite shown for caps with no icon (no DeckSprite, no sticker).")]
    [SerializeField] private Sprite _fallbackCapSprite;

    [Tooltip("The Canvas that the tooltip renders on. If null, auto-found.")]
    [SerializeField] private Canvas _uiCanvas;

    [Tooltip("If true, the panel auto-hides when the user clicks anywhere outside it " +
             "(after it has been shown). Useful for dismissing the rewards before " +
             "clicking 'Next Level'.")]
    [SerializeField] private bool _autoHideOnClick = false;

    // --- Per-entry data (mirrors DeckPanelUI's structure) ---
    struct RewardEntry
    {
        public CapSnapshot Snapshot;
        public GameObject EntryObj;
        public Image CapIcon;
        public RectTransform CapIconRT;
        public RectTransform EntryRT;
        public List<Image> StickerImages;
        public List<string> StickerDescriptions;
    }

    private readonly List<RewardEntry> _lostEntries = new();
    private readonly List<RewardEntry> _gainedEntries = new();
    private GameObject _tooltipInstance;
    private HintView _hintView;
    private RectTransform _tooltipRect;

    private RunManager _runManager;

    void Awake()
    {
        if (_uiCanvas == null) _uiCanvas = GetComponentInParent<Canvas>();
        if (_uiCanvas == null) _uiCanvas = FindFirstObjectByType<Canvas>();
        _runManager = RunManager.Instance;
    }

    void OnEnable()
    {
        SubscribeToRunManager();
    }

    void OnDisable()
    {
        UnsubscribeFromRunManager();
        HideTooltip();
    }

    void Start()
    {
        // Set up the tooltip instance.
        if (_tooltipPrefab != null && _uiCanvas != null)
        {
            _tooltipInstance = Instantiate(_tooltipPrefab, _uiCanvas.transform);
            _tooltipInstance.SetActive(false);
            _hintView = _tooltipInstance.GetComponentInChildren<HintView>(true);
            if (_hintView == null)
                _hintView = _tooltipInstance.AddComponent<HintView>();
            _tooltipRect = _tooltipInstance.transform as RectTransform;
        }

        // Panel starts hidden.
        // IMPORTANT: do NOT auto-populate from RunManager.LastBattleResult here.
        // When the player advances to the next level, the new scene's MatchRewardsPanel
        // would see the PREVIOUS battle's LastBattleResult (RunManager persists via
        // DontDestroyOnLoad) and auto-show the panel — making it look like the result
        // panel "didn't hide itself". The panel should ONLY populate when a NEW
        // OnBattleEnded event fires (i.e., when a battle actually ends in this scene).
        if (_rootPanel != null)
            _rootPanel.SetActive(false);
    }

    void SubscribeToRunManager()
    {
        if (_runManager == null) _runManager = RunManager.Instance;
        if (_runManager != null)
            _runManager.OnBattleEnded += HandleBattleEnded;
    }

    void UnsubscribeFromRunManager()
    {
        if (_runManager != null)
            _runManager.OnBattleEnded -= HandleBattleEnded;
    }

    // -----------------------------------------------------------------------
    // Event handling
    // -----------------------------------------------------------------------

    void HandleBattleEnded(BattleResult result)
    {
        if (result == null) return;
        Populate(result);
    }

    // -----------------------------------------------------------------------
    // Populating the panel
    // -----------------------------------------------------------------------

    /// <summary>
    /// Populates the panel from a BattleResult and activates it.
    /// </summary>
    public void Populate(BattleResult result)
    {
        if (result == null) return;

        ClearEntries();

        // Lost section.
        int lostCount = result.LostCaps != null ? result.LostCaps.Count : 0;
        bool hasLost = lostCount > 0;
        if (_lostSection != null) _lostSection.SetActive(hasLost);
        if (hasLost && _lostContentParent != null)
        {
            for (int i = 0; i < lostCount; i++)
                _lostEntries.Add(CreateEntry(result.LostCaps[i], _lostContentParent));
        }

        // Gained section.
        int gainedCount = result.GainedCaps != null ? result.GainedCaps.Count : 0;
        bool hasGained = gainedCount > 0;
        if (_gainedSection != null) _gainedSection.SetActive(hasGained);
        if (hasGained && _gainedContentParent != null)
        {
            for (int i = 0; i < gainedCount; i++)
                _gainedEntries.Add(CreateEntry(result.GainedCaps[i], _gainedContentParent));
        }

        // Update count text.
        if (_lostCountText != null)
            _lostCountText.text = hasLost ? $"-{lostCount}" : "0";
        if (_gainedCountText != null)
            _gainedCountText.text = hasGained ? $"+{gainedCount}" : "0";

        // If both sections are empty, still show the panel (the result text
        // might say "no caps changed" — but we don't manage that text here).
        // If the designer only wants to show the panel when there's something
        // to show, they can disable the panel via the result.
        if (_rootPanel != null)
            _rootPanel.SetActive(true);

        // Wait one frame for the layout to settle, then apply the overlap layout.
        // (If we call it now, the RectTransforms haven't been computed yet.)
        // We use a coroutine-like approach via Invoke.
        // For simplicity, we'll do an immediate layout pass here, and another
        // in Update() on the first frame.
        LayoutEntries(_lostEntries, _lostContentParent);
        LayoutEntries(_gainedEntries, _gainedContentParent);
    }

    RewardEntry CreateEntry(CapSnapshot snapshot, RectTransform parent)
    {
        GameObject entryObj = Instantiate(_capEntryPrefab, parent);
        Image capIcon = entryObj.GetComponent<Image>();
        if (capIcon == null) capIcon = entryObj.GetComponentInChildren<Image>();
        RectTransform capIconRT = capIcon != null ? capIcon.rectTransform : null;

        var entry = new RewardEntry
        {
            Snapshot = snapshot,
            EntryObj = entryObj,
            CapIcon = capIcon,
            CapIconRT = capIconRT,
            EntryRT = entryObj.transform as RectTransform,
            StickerImages = new List<Image>(),
            StickerDescriptions = new List<string>()
        };

        // Cap icon.
        if (capIcon != null)
        {
            Sprite capSprite = snapshot.IconSprite;
            if (capSprite == null) capSprite = _fallbackCapSprite;
            if (capSprite != null)
                capIcon.sprite = capSprite;
            capIcon.preserveAspect = true;
        }

        // Force the entry's size to _capEntrySize so the overlap layout
        // knows the exact footprint.
        if (entry.EntryRT != null)
            entry.EntryRT.sizeDelta = _capEntrySize;

        // Stickers.
        if (_stickerImagePrefab != null && snapshot.Stickers != null)
        {
            for (int s = 0; s < snapshot.Stickers.Count; s++)
            {
                StickerSnapshot sticker = snapshot.Stickers[s];
                if (sticker.Sprite == null) continue;

                GameObject stickerObj = Instantiate(_stickerImagePrefab, entryObj.transform);
                Image stickerImg = stickerObj.GetComponent<Image>();
                if (stickerImg == null) stickerImg = stickerObj.GetComponentInChildren<Image>();
                if (stickerImg == null) stickerImg = stickerObj.AddComponent<Image>();
                stickerImg.sprite = sticker.Sprite;
                stickerImg.preserveAspect = true;
                stickerImg.raycastTarget = false; // we do our own hover detection

                RectTransform srt = stickerObj.transform as RectTransform;
                if (srt != null)
                    srt.sizeDelta = _stickerSize;
                stickerObj.SetActive(true);

                // Level badge (x2/x3).
                StickerView view = stickerObj.GetComponent<StickerView>();
                if (view == null)
                    view = stickerObj.AddComponent<StickerView>();
                view.SetLevel(sticker.Level);

                entry.StickerImages.Add(stickerImg);
                entry.StickerDescriptions.Add(sticker.Description ?? string.Empty);
            }
        }

        return entry;
    }

    void ClearEntries()
    {
        ClearList(_lostEntries);
        ClearList(_gainedEntries);
    }

    static void ClearList(List<RewardEntry> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].EntryObj != null)
                Destroy(list[i].EntryObj);
        }
        list.Clear();
    }

    // -----------------------------------------------------------------------
    // Overlap layout
    // -----------------------------------------------------------------------

    /// <summary>
    /// Positions entries horizontally inside the content parent. If they all
    /// fit at the natural spacing (_capEntrySize + _capSpacing), they're
    /// spaced out and centered. If they DON'T fit, the spacing shrinks so they
    /// overlap (each entry partially covers the previous one).
    ///
    /// The overlap step is computed so the row spans exactly the available
    /// width. With N entries of width W and panel width P, the step is
    /// (P - W) / (N - 1) when N > 1 (step &lt; W → overlap).
    /// </summary>
    void LayoutEntries(List<RewardEntry> entries, RectTransform contentParent)
    {
        if (entries == null || entries.Count == 0) return;
        if (contentParent == null) return;

        // Force a layout rebuild so the parent's rect is up to date.
        UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(contentParent);

        float panelWidth = contentParent.rect.width;
        float capW = _capEntrySize.x;
        int count = entries.Count;

        // Natural total width = N caps + (N-1) gaps.
        float naturalTotal = count * capW + (count - 1) * _capSpacing;

        float step;
        if (naturalTotal <= panelWidth)
        {
            // Fits — use natural spacing.
            step = capW + _capSpacing;
        }
        else
        {
            // Doesn't fit — overlap. Spread caps across the panel width.
            // step = (panelWidth - capW) / (count - 1)
            // step < capW → overlap (entries partially cover each other).
            if (count > 1)
                step = Mathf.Max(0f, (panelWidth - capW) / (count - 1));
            else
                step = 0f;
        }

        // Center the row.
        float totalUsed = (count - 1) * step + capW;
        float startX = (panelWidth - totalUsed) * 0.5f;

        for (int i = 0; i < count; i++)
        {
            RewardEntry entry = entries[i];
            if (entry.EntryRT == null) continue;

            // Position the entry's CENTER at startX + i * step + capW/2.
            float centerX = startX + i * step + capW * 0.5f;
            // anchoredPosition is relative to the entry's anchor. We assume
            // the entry is anchored to the left edge with pivot (0.5, 0.5).
            // If the prefab uses a different anchor, set it here:
            entry.EntryRT.anchorMin = new Vector2(0f, 0.5f);
            entry.EntryRT.anchorMax = new Vector2(0f, 0.5f);
            entry.EntryRT.pivot = new Vector2(0.5f, 0.5f);
            entry.EntryRT.anchoredPosition = new Vector2(centerX, _verticalOffset);

            // When overlapping, later entries (i.e., higher index) should render
            // on TOP of earlier ones. In Unity's UGUI hierarchy, later siblings
            // render on top. Since we Instantiate them in order, the natural
            // hierarchy order already does this — entry i is a sibling AFTER
            // entry i-1, so entry i renders on top. No SetSiblingIndex needed.
        }

        // After positioning entries, place stickers radially around each cap icon.
        UpdateStickerPositions(entries, contentParent);
    }

    /// <summary>
    /// Per-frame: positions each sticker radially around its cap icon.
    /// Same logic as DeckPanelUI.UpdateStickerPositions — but here we run it
    /// every frame because the layout can shift as the panel settles or the
    /// window resizes.
    /// </summary>
    void UpdateStickerPositions(List<RewardEntry> entries, RectTransform contentParent)
    {
        Camera uiCamera = (_uiCanvas != null && _uiCanvas.renderMode == RenderMode.ScreenSpaceCamera)
            ? _uiCanvas.worldCamera
            : null;

        for (int e = 0; e < entries.Count; e++)
        {
            RewardEntry entry = entries[e];
            if (entry.CapIconRT == null || entry.StickerImages == null || entry.EntryRT == null) continue;

            // Cap icon center in screen pixels.
            Vector3 capIconScreenCenter = RectTransformUtility.WorldToScreenPoint(uiCamera, entry.CapIconRT.position);
            // Entry center in screen pixels.
            Vector3 entryScreenCenter = RectTransformUtility.WorldToScreenPoint(uiCamera, entry.EntryRT.position);

            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                entry.EntryRT, capIconScreenCenter, uiCamera, out Vector2 capIconLocal);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                entry.EntryRT, entryScreenCenter, uiCamera, out Vector2 entryLocal);
            Vector2 capIconOffset = capIconLocal - entryLocal;

            int stickerCount = entry.StickerImages.Count;
            for (int s = 0; s < stickerCount; s++)
            {
                Image stickerImg = entry.StickerImages[s];
                if (stickerImg == null) continue;
                RectTransform srt = stickerImg.rectTransform;

                float angleDeg = s * _stickerAngleStep;
                float angleRad = angleDeg * Mathf.Deg2Rad;
                Vector2 dir = new Vector2(-Mathf.Sin(angleRad), Mathf.Cos(angleRad));

                Vector2 stickerLocal = capIconOffset + dir * _stickerRadiusPixels;
                srt.anchoredPosition = stickerLocal;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Per-frame
    // -----------------------------------------------------------------------

    void Update()
    {
        // Always update sticker positions (they need to follow the cap icons
        // if the layout shifts due to resize / panel animations).
        UpdateStickerPositions(_lostEntries, _lostContentParent);
        UpdateStickerPositions(_gainedEntries, _gainedContentParent);

        // Sticker hover.
        HandleStickerHover();

        // Optional auto-hide on click outside the panel.
        if (_autoHideOnClick && _rootPanel != null && _rootPanel.activeSelf)
        {
            if (Mouse.current?.leftButton.wasPressedThisFrame == true && !IsCursorOverAnyContent())
            {
                _rootPanel.SetActive(false);
            }
        }
    }

    bool IsCursorOverAnyContent()
    {
        if (Mouse.current == null) return false;
        Vector2 mousePos = Mouse.current.position.ReadValue();
        Camera uiCamera = (_uiCanvas != null && _uiCanvas.renderMode == RenderMode.ScreenSpaceCamera)
            ? _uiCanvas.worldCamera
            : null;
        if (_lostContentParent != null && RectTransformUtility.RectangleContainsScreenPoint(_lostContentParent, mousePos, uiCamera))
            return true;
        if (_gainedContentParent != null && RectTransformUtility.RectangleContainsScreenPoint(_gainedContentParent, mousePos, uiCamera))
            return true;
        // Also count the panel root itself as "over content" so clicking the
        // background doesn't dismiss the panel.
        if (_rootPanel != null)
        {
            RectTransform rootRT = _rootPanel.transform as RectTransform;
            if (rootRT != null && RectTransformUtility.RectangleContainsScreenPoint(rootRT, mousePos, uiCamera))
                return true;
        }
        return false;
    }

    void HandleStickerHover()
    {
        if (_tooltipInstance == null || Mouse.current == null) return;

        Vector2 mousePos = Mouse.current.position.ReadValue();
        bool foundHover = false;
        Vector2 hoveredStickerScreenPos = default;
        string hoveredDescription = string.Empty;

        Camera uiCamera = (_uiCanvas != null && _uiCanvas.renderMode == RenderMode.ScreenSpaceCamera)
            ? _uiCanvas.worldCamera
            : null;

        float stickerVisualScale = ((_stickerSize.x + _stickerSize.y) * 0.5f) / 32f;
        float scaledHoverRadius = _stickerHoverRadius * stickerVisualScale;

        // Check lost and gained entries.
        if (TryFindHoveredSticker(_lostEntries, uiCamera, mousePos, scaledHoverRadius,
                out hoveredStickerScreenPos, out hoveredDescription))
        {
            foundHover = true;
        }
        else if (TryFindHoveredSticker(_gainedEntries, uiCamera, mousePos, scaledHoverRadius,
                     out hoveredStickerScreenPos, out hoveredDescription))
        {
            foundHover = true;
        }

        if (foundHover)
            ShowTooltip(hoveredDescription, hoveredStickerScreenPos);
        else
            HideTooltip();
    }

    static bool TryFindHoveredSticker(
        List<RewardEntry> entries,
        Camera uiCamera,
        Vector2 mousePos,
        float scaledHoverRadius,
        out Vector2 stickerScreenPos,
        out string description)
    {
        stickerScreenPos = default;
        description = string.Empty;

        for (int e = 0; e < entries.Count; e++)
        {
            RewardEntry entry = entries[e];
            if (entry.StickerImages == null) continue;

            for (int s = 0; s < entry.StickerImages.Count; s++)
            {
                Image sticker = entry.StickerImages[s];
                if (sticker == null) continue;

                RectTransform stickerRT = sticker.rectTransform;
                Vector3 stickerWorldPos = stickerRT.position;
                Vector3 stickerScreen = RectTransformUtility.WorldToScreenPoint(uiCamera, stickerWorldPos);

                float dist = Vector2.Distance(mousePos, new Vector2(stickerScreen.x, stickerScreen.y));
                if (dist <= scaledHoverRadius)
                {
                    stickerScreenPos = new Vector2(stickerScreen.x, stickerScreen.y);
                    if (s < entry.StickerDescriptions.Count)
                        description = entry.StickerDescriptions[s];
                    return true;
                }
            }
        }
        return false;
    }

    void ShowTooltip(string text, Vector2 stickerScreenPos)
    {
        if (_tooltipInstance == null || _tooltipRect == null) return;
        if (string.IsNullOrEmpty(text)) return;

        _tooltipInstance.SetActive(true);
        if (_hintView != null)
            _hintView.SetText(text);

        UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(_tooltipRect);

        Vector2 tooltipSize = _tooltipRect.rect.size * _tooltipRect.lossyScale.x;
        float sw = Screen.width;
        float sh = Screen.height;

        Camera uiCamera = (_uiCanvas != null && _uiCanvas.renderMode == RenderMode.ScreenSpaceCamera)
            ? _uiCanvas.worldCamera
            : null;

        // Try the offset, then the negated offset if offscreen. If both go
        // offscreen (e.g., tooltip too wide), use the negated offset and
        // clamp X so the tooltip stays horizontally on screen.
        Vector2[] offsets = { _hintOffset, -_hintOffset };

        foreach (Vector2 offset in offsets)
        {
            Vector2 target = stickerScreenPos + offset;
            bool onScreen = target.x >= 0f && target.y >= 0f &&
                            target.x + tooltipSize.x <= sw &&
                            target.y + tooltipSize.y <= sh;
            if (!onScreen) continue;

            RectTransform parentRT = _tooltipRect.parent as RectTransform;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                parentRT, target, uiCamera, out Vector2 localPos);
            _tooltipRect.anchoredPosition = localPos;
            return;
        }

        // Both offsets offscreen — clamp X.
        {
            Vector2 target = stickerScreenPos - _hintOffset;
            if (target.x < 0f) target.x = 0f;
            if (target.x + tooltipSize.x > sw) target.x = sw - tooltipSize.x;
            RectTransform parentRT = _tooltipRect.parent as RectTransform;
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

    // -----------------------------------------------------------------------
    // Public API (for external callers, e.g., BattleResultUI)
    // -----------------------------------------------------------------------

    /// <summary>Shows the rewards panel (does not repopulate).</summary>
    public void Show()
    {
        if (_rootPanel != null)
            _rootPanel.SetActive(true);
    }

    /// <summary>Hides the rewards panel.</summary>
    public void Hide()
    {
        if (_rootPanel != null)
            _rootPanel.SetActive(false);
        HideTooltip();
    }

    /// <summary>True if the rewards panel is currently showing.</summary>
    public bool IsVisible => _rootPanel != null && _rootPanel.activeSelf;
}
