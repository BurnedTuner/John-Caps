using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

/// <summary>
/// A toggle button + pop-out panel showing the player's remaining deck.
///
/// Each cap gets its OWN entry — caps are never stacked into a single slot
/// with a count badge. An entry consists of:
///   - The cap's unique DeckSprite (Cap._deckSprite, set per-prefab in inspector).
///     Falls back to the first ICapAbility.StickerSprite, then to _fallbackCapSprite.
///   - Below/around the cap icon: a small sticker Image per ICapAbility
///     (rendered WITH the cap, so the player sees the cap's abilities at a glance).
///
/// Hovering a sticker shows a hint tooltip with the ability's Description.
/// Hovering the cap icon itself shows nothing (the cap icon is just an icon).
///
/// The panel BLOCKS hover detection on caps visually behind it — when the
/// deck panel is open, StickerManager's GetHoveredFieldCap/GetHoveredHandCap
/// return null if the cursor is over the panel. This is implemented via
/// <see cref="IsCursorOverPanel"/>, which StickerManager queries.
///
/// DATA SOURCE:
///   - Source.CapHand (default): reads from the battle scene's CapHand — the
///     live deck that gets drawn into the hand. Used in battle scenes.
///   - Source.RunManager: reads from RunManager.RunDeck — the persistent deck
///     that survives scene transitions. Used in the RunProgress scene (which
///     has no CapHand, just RunManager).
///
/// Setup in Unity:
/// 1. Create a Canvas with:
///    - A Button (the "deck" toggle button — always visible).
///    - A Panel (the deck contents — shown/hidden when the button is clicked).
///      The Panel should have a Raycast Target on its Image so IsCursorOverPanel
///      works (Unity's RectTransformUtility.RectangleContainsScreenPoint needs
///      the panel to receive raycasts to be reliable, but the implementation
///      here uses screen-point-in-rect which works regardless of raycast target).
///    - Inside the panel: a ScrollRect → Viewport → Content with a
///      HorizontalOrVerticalLayoutGroup (VerticalLayoutGroup recommended so
///      entries stack top-to-bottom and cap icons + their stickers fit).
/// 2. Prefabs (assign in inspector):
///    - _capEntryPrefab: a UI prefab representing ONE deck entry. Root should have
///      an Image (the cap icon). Stickers are positioned radially around the
///      cap icon's screen position — no StickerContainer child is needed (the
///      script positions stickers manually, mirroring how StickerManager places
///      stickers radially around field/hand caps, but on a flat UI plane).
///    - _stickerImagePrefab: a UI Image prefab (one per ability sticker).
/// 3. Tooltip: assign _tooltipPrefab (same prefab as StickerManager uses —
///    should have a HintView component). If null, tooltips are disabled.
/// 4. Add DeckPanelUI to any GameObject. Assign references. Done.
/// 5. Set _source to CapHand (battle scene) or RunManager (progress scene).
///    In CapHand mode, _capHand is auto-found if null.
///    In RunManager mode, _capHand is ignored (RunManager.Instance is used).
/// </summary>
public class DeckPanelUI : MonoBehaviour
{
    /// <summary>
    /// Where the deck panel reads its cap data from.
    /// </summary>
    public enum Source
    {
        /// <summary>
        /// Read from the battle scene's CapHand. Used in battle scenes.
        /// </summary>
        CapHand = 0,

        /// <summary>
        /// Read from RunManager.RunDeck. Used in the RunProgress scene (which
        /// has no CapHand — only RunManager.Instance, which persists via
        /// DontDestroyOnLoad).
        /// </summary>
        RunManager = 1,
    }

    [Header("Data source")]
    [Tooltip("Where to read the deck from. CapHand = battle scene's live hand " +
             "(auto-found if _capHand is null). RunManager = persistent run deck " +
             "(used in the RunProgress scene).")]
    [SerializeField] private Source _source = Source.CapHand;

    [Header("References")]
    [Tooltip("The button that toggles the deck panel open/closed. Its onClick is auto-wired. " +
             "Also reacts to the E key being held (visual feedback).")]
    [SerializeField] private Button _toggleButton;

    [Tooltip("The panel GameObject shown/hidden when the toggle button is clicked. Set inactive by default.")]
    [SerializeField] private GameObject _panel;

    [Tooltip("The panel's RectTransform (used for hover-blocking via screen-point-in-rect). " +
             "If null, falls back to _panel.GetComponent<RectTransform>().")]
    [SerializeField] private RectTransform _panelRect;

    [Tooltip("The player's CapHand. If null, auto-found.")]
    [SerializeField] private CapHand _capHand;

    [Tooltip("Optional: GameManager for board-reset events. Auto-found if null.")]
    [SerializeField] private GameManager _gameManager;

    [Tooltip("Optional: CapTurnResolver for turn-finished events (to refresh the count " +
             "when a cap is drawn from the deck after a throw resolves). Auto-found if null.")]
    [SerializeField] private CapTurnResolver _turnResolver;

    [Header("Keyboard feedback")]
    [Tooltip("Color applied to the toggle button when the E key is held.")]
    [SerializeField] private Color _keyHeldColor = new Color(0.6f, 0.8f, 1f, 1f);

    [Header("Prefabs")]
    [Tooltip("Prefab for one deck entry. Root should have an Image (the cap icon). " +
             "Stickers are positioned radially around the cap icon by this script — " +
             "NO StickerContainer child is needed. The cap entry prefab can be just a " +
             "single Image, or an Image with other decorative children (which this " +
             "script will leave alone).")]
    [SerializeField] private GameObject _capEntryPrefab;

    [Tooltip("Prefab for a sticker Image (one per ICapAbility on the cap).")]
    [SerializeField] private GameObject _stickerImagePrefab;

    [Tooltip("Tooltip prefab (same as StickerManager uses — should have a HintView). Optional.")]
    [SerializeField] private GameObject _tooltipPrefab;

    [Header("Layout")]
    [Tooltip("Parent RectTransform inside the panel where cap entries are instantiated. " +
             "Typically the Content of a ScrollRect. Should have a VerticalLayoutGroup.")]
    [SerializeField] private RectTransform _contentParent;

    [Tooltip("Fallback sprite shown for caps with no DeckSprite and no ICapAbility sticker. Optional.")]
    [SerializeField] private Sprite _fallbackCapSprite;

    [Tooltip("Screen-space radius (pixels) for sticker hover detection in the deck panel. " +
             "This is the BASE radius — it's scaled by the sticker's visual size so bigger " +
             "stickers get a bigger hover area. The scale factor is the sticker's average " +
             "dimension divided by 32 (so a 32x32 sticker uses the base radius, a 64x64 " +
             "sticker uses 2x the base radius).")]
    [SerializeField] private float _stickerHoverRadius = 32f;

    [Tooltip("Screen-space offset of the hint tooltip relative to the hovered sticker. " +
             "Same logic as StickerManager: tries this offset, then the negated offset if " +
             "the tooltip would go offscreen.")]
    [SerializeField] private Vector2 _hintOffset = new Vector2(0f, 60f);

    [Tooltip("Degrees between stickers around the cap icon. First sticker at 0° (top), " +
             "then counter-clockwise. Mirrors StickerManager._stickerAngleStep.")]
    [SerializeField] private float _stickerAngleStep = 45f;

    [Tooltip("Radius (pixels) of the circle on which stickers sit around the cap icon's " +
             "center. This is the flat-UI-plane equivalent of StickerManager's world-space " +
             "sticker radius — same radial positioning logic, but in screen pixels instead " +
             "of world units.")]
    [SerializeField] private float _stickerRadiusPixels = 48f;

    [Tooltip("Size (pixels) of each sticker Image in the deck panel. The sticker prefab's " +
             "RectTransform.sizeDelta is overridden with this value when the sticker is " +
             "instantiated. Change this to make deck stickers bigger or smaller without " +
             "editing the sticker prefab itself.")]
    [Min(1f)] [SerializeField] private Vector2 _stickerSize = new Vector2(32f, 32f);

    [Tooltip("Optional: text showing the count (e.g., 'Deck: 5'). Updated on refresh.")]
    [SerializeField] private TMPro.TMP_Text _countText;

    [Tooltip("The Canvas that the tooltip renders on. If null, uses the panel's canvas or FindFirstObjectByType.")]
    [SerializeField] private Canvas _uiCanvas;

    // Per-entry data: cap, the spawned entry GameObject, the cap icon Image,
    // and the sticker Images + their cached screen positions (recomputed each
    // frame in UpdateStickerPositions, since the layout can shift as entries
    // are added/removed or the panel is scrolled).
    struct DeckEntry
    {
        public Cap Cap;
        public GameObject EntryObj;
        public Image CapIcon;
        public RectTransform CapIconRT;
        public List<Image> StickerImages;
        public List<string> StickerDescriptions;
    }

    private readonly List<DeckEntry> _entries = new();
    private GameObject _tooltipInstance;
    private HintView _hintView;
    private RectTransform _tooltipRect;

    // Cached for hover-blocking queries from StickerManager.
    private static DeckPanelUI _activeInstance;

    // Last deck count reported to _countText. Polled every frame in Update so
    // the count updates immediately when a cap is drawn (without relying on
    // OnTurnFinished firing — which can be unreliable if the resolver isn't
    // found in time, or if the hand draws from the deck at an unexpected moment).
    private int _lastReportedCount = -1;

    // Cached original toggle button color (for keyboard visual feedback).
    private Color _toggleNormalColor = Color.white;
    private bool _toggleColorCached;

    void Awake()
    {
        if (_capHand == null) _capHand = FindFirstObjectByType<CapHand>();
        if (_gameManager == null) _gameManager = FindFirstObjectByType<GameManager>();
        if (_turnResolver == null) _turnResolver = FindFirstObjectByType<CapTurnResolver>();
        if (_panelRect == null && _panel != null)
            _panelRect = _panel.GetComponent<RectTransform>();
        if (_uiCanvas == null) _uiCanvas = GetComponentInParent<Canvas>();
        if (_uiCanvas == null) _uiCanvas = FindFirstObjectByType<Canvas>();
    }

    void OnEnable()
    {
        if (_toggleButton != null)
            _toggleButton.onClick.AddListener(TogglePanel);
        if (_gameManager != null)
            _gameManager.OnBoardReset += HandleBoardReset;
        if (_turnResolver != null)
            _turnResolver.OnTurnFinished += HandleTurnFinished;
    }

    void OnDisable()
    {
        if (_toggleButton != null)
            _toggleButton.onClick.RemoveListener(TogglePanel);
        if (_gameManager != null)
            _gameManager.OnBoardReset -= HandleBoardReset;
        if (_turnResolver != null)
            _turnResolver.OnTurnFinished -= HandleTurnFinished;
        if (_activeInstance == this)
            _activeInstance = null;
        HideTooltip();
    }

    void Start()
    {
        // Set up tooltip instance.
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
        if (_panel != null)
            _panel.SetActive(false);
        UpdateCountText();
    }

    /// <summary>Toggle the deck panel open/closed. Refreshes contents when opening.</summary>
    public void TogglePanel()
    {
        if (_panel == null) return;
        bool newState = !_panel.activeSelf;
        _panel.SetActive(newState);
        if (newState)
        {
            _activeInstance = this;
            Refresh();
        }
        else
        {
            if (_activeInstance == this)
                _activeInstance = null;
            HideTooltip();
        }
    }

    /// <summary>Force-open the panel and refresh.</summary>
    public void Open()
    {
        if (_panel == null) return;
        _panel.SetActive(true);
        _activeInstance = this;
        Refresh();
    }

    /// <summary>Force-close the panel.</summary>
    public void Close()
    {
        if (_panel != null)
            _panel.SetActive(false);
        if (_activeInstance == this)
            _activeInstance = null;
        HideTooltip();
    }

    /// <summary>
    /// True if the deck panel is currently open AND the cursor is over it.
    /// Queried by StickerManager to block hover detection on caps visually
    /// behind the panel (field caps, hand caps stickers). When this returns
    /// true, StickerManager should skip its hover logic for the frame.
    /// </summary>
    public static bool IsCursorOverPanel
    {
        get
        {
            if (_activeInstance == null) return false;
            return _activeInstance.IsCursorOverThisPanel();
        }
    }

    /// <summary>
    /// True if the cursor is inside the panel's screen rect. Uses the cached
    /// _panelRect. Returns false if the panel is closed or the rect is null.
    /// </summary>
    bool IsCursorOverThisPanel()
    {
        if (_panel == null || !_panel.activeSelf || _panelRect == null)
            return false;
        if (Mouse.current == null) return false;
        Vector2 mousePos = Mouse.current.position.ReadValue();
        Camera uiCamera = (_uiCanvas != null && _uiCanvas.renderMode == RenderMode.ScreenSpaceCamera)
            ? _uiCanvas.worldCamera
            : null;
        return RectTransformUtility.RectangleContainsScreenPoint(_panelRect, mousePos, uiCamera);
    }

    void HandleBoardReset(GameManager _)
    {
        if (_panel != null && _panel.activeSelf)
            Refresh();
        UpdateCountText();
    }

    /// <summary>
    /// Called when a throw resolves (cap may have been drawn from the deck to
    /// refill the hand). Updates the count text immediately so the player sees
    /// the deck shrink without having to open the panel. Also refreshes the
    /// panel contents if it's currently open.
    /// </summary>
    void HandleTurnFinished(CapTurnResolver _)
    {
        if (_panel != null && _panel.activeSelf)
            Refresh();
        UpdateCountText();
    }

    void Update()
    {
        // E key toggles the deck panel (alternative to the on-screen button).
        if (Keyboard.current?.eKey.wasPressedThisFrame == true)
        {
            TogglePanel();
            UIButtonSound.PlayClick();
        }

        // Visual feedback: if the on-screen toggle button is assigned, change
        // its color while E is held (same pattern as PrecisionAimUI).
        if (_toggleButton != null && _toggleButton.image != null)
        {
            if (!_toggleColorCached)
            {
                _toggleNormalColor = _toggleButton.image.color;
                _toggleColorCached = true;
            }
            bool eHeld = Keyboard.current?.eKey.isPressed == true;
            _toggleButton.image.color = eHeld ? _keyHeldColor : _toggleNormalColor;
        }

        // Always poll the deck count — the count text should update immediately
        // when a cap is drawn from the deck, regardless of whether the panel
        // is open. We poll instead of relying solely on OnTurnFinished because
        // the resolver reference might be found late, or the hand might draw
        // at an unexpected moment (e.g., on board reset).
        PollDeckCount();

        // Only handle sticker positioning + hover when the panel is open.
        if (_panel == null || !_panel.activeSelf) return;
        UpdateStickerPositions();
        HandleStickerHover();
    }

    /// <summary>
    /// Checks if the deck count changed since last frame. If so, updates the
    /// count text. Also refreshes the panel contents if it's open (so the icon
    /// list stays in sync when caps are drawn).
    /// </summary>
    void PollDeckCount()
    {
        int currentCount = CurrentDeckCount;
        if (currentCount != _lastReportedCount)
        {
            _lastReportedCount = currentCount;
            UpdateCountText();
            // If the panel is open, the icon list is now stale — refresh it
            // so a drawn cap disappears from the view immediately.
            if (_panel != null && _panel.activeSelf)
                Refresh();
        }
    }

    /// <summary>
    /// Rebuilds the entry list from the current deck. Destroys old entries,
    /// instantiates one per remaining deck cap. No-op if the cap entry prefab
    /// or content parent is missing.
    ///
    /// Branches by _source: CapHand reads live cap instances (with already-set
    /// generated sprites). RunManager reads DeckEntry prefab references + stored
    /// sprites (no live cap instance needed — works in the progress scene
    /// where CapHand doesn't exist).
    /// </summary>
    void Refresh()
    {
        if (_capEntryPrefab == null || _contentParent == null)
        {
            UpdateCountText();
            return;
        }

        // Clear old entries.
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].EntryObj != null)
                Destroy(_entries[i].EntryObj);
        }
        _entries.Clear();

        if (_source == Source.RunManager)
            RefreshFromRunManager();
        else
            RefreshFromCapHand();

        // Position stickers radially around each cap icon NOW (and again every
        // frame in Update) so they appear in the right place immediately.
        UpdateStickerPositions();

        UpdateCountText();
    }

    /// <summary>
    /// Reads from CapHand.DeckCount + GetDeckCap(i). Each preview cap already
    /// has its generated visuals (Cap.Configure → GenerateVisuals was called
    /// when CapHand.ResetHand created the preview caps).
    /// </summary>
    void RefreshFromCapHand()
    {
        if (_capHand == null)
        {
            _capHand = FindFirstObjectByType<CapHand>();
            if (_capHand == null) return;
        }

        // Spawn ONE entry per remaining deck cap — never stack similar caps
        // into a single slot. Each cap gets its own entry, even if multiple
        // caps share the same prefab.
        int count = _capHand.DeckCount;
        for (int i = 0; i < count; i++)
        {
            Cap cap = _capHand.GetDeckCap(i);
            if (cap == null) continue;

            GameObject entryObj = Instantiate(_capEntryPrefab, _contentParent);
            Image capIcon = entryObj.GetComponent<Image>();
            if (capIcon == null) capIcon = entryObj.GetComponentInChildren<Image>();
            RectTransform capIconRT = capIcon != null ? capIcon.rectTransform : null;

            var entry = new DeckEntry
            {
                Cap = cap,
                EntryObj = entryObj,
                CapIcon = capIcon,
                CapIconRT = capIconRT,
                StickerImages = new List<Image>(),
                StickerDescriptions = new List<string>()
            };

            // Set the cap icon sprite: DeckSprite → first sticker → fallback.
            if (capIcon != null)
            {
                Sprite capSprite = cap.DeckSprite;
                if (capSprite == null)
                {
                    var fallbackAbilities = cap.GetComponents<ICapAbility>();
                    for (int a = 0; a < fallbackAbilities.Length; a++)
                    {
                        if (fallbackAbilities[a] != null && fallbackAbilities[a].StickerSprite != null)
                        {
                            capSprite = fallbackAbilities[a].StickerSprite;
                            break;
                        }
                    }
                }
                if (capSprite == null) capSprite = _fallbackCapSprite;
                if (capSprite != null)
                    capIcon.sprite = capSprite;
            }

            // Spawn sticker images for each ICapAbility on the cap.
            if (_stickerImagePrefab != null)
            {
                var abilities = cap.GetComponents<ICapAbility>();
                for (int a = 0; a < abilities.Length; a++)
                {
                    if (abilities[a] == null) continue;
                    Sprite stickerSprite = abilities[a].StickerSprite;
                    if (stickerSprite == null) continue;

                    GameObject stickerObj = Instantiate(_stickerImagePrefab, entryObj.transform);
                    Image stickerImg = stickerObj.GetComponent<Image>();
                    if (stickerImg == null) stickerImg = stickerObj.GetComponentInChildren<Image>();
                    if (stickerImg == null) stickerImg = stickerObj.AddComponent<Image>();
                    stickerImg.sprite = stickerSprite;
                    stickerImg.preserveAspect = true;
                    stickerImg.raycastTarget = false;

                    RectTransform srt = stickerObj.transform as RectTransform;
                    if (srt != null)
                        srt.sizeDelta = _stickerSize;
                    stickerObj.SetActive(true);

                    StickerView view = stickerObj.GetComponent<StickerView>();
                    if (view == null)
                        view = stickerObj.AddComponent<StickerView>();
                    view.SetLevel(abilities[a].Level);

                    entry.StickerImages.Add(stickerImg);
                    entry.StickerDescriptions.Add(abilities[a].Description ?? string.Empty);
                }
            }

            _entries.Add(entry);
        }
    }

    /// <summary>
    /// Reads from RunManager.Instance.RunDeck. Each DeckEntry stores the base
    /// prefab + ability levels + generated face/back sprites. Stickers are
    /// read from the deck's ABILITY TEMPLATE prefabs (which have the ability
    /// components configured with sticker sprites + descriptions), NOT from
    /// the base prefab (which is visual-only and has no ability components).
    ///
    /// No live CapHand is needed — this works in the RunProgress scene where
    /// only RunManager.Instance (DontDestroyOnLoad) is present.
    /// </summary>
    void RefreshFromRunManager()
    {
        RunManager runManager = RunManager.Instance;
        if (runManager == null || runManager.RunDeck == null) return;

        // Get the CapDeckDefinition from the LevelSequence — it has the
        // ability template prefabs (which have sticker sprites + descriptions).
        CapDeckDefinition deckAsset = runManager.LevelSequence != null
            ? runManager.LevelSequence.StartingPlayerDeck
            : null;

        var runDeck = runManager.RunDeck;
        for (int i = 0; i < runDeck.Count; i++)
        {
            var deckEntry = runDeck[i];
            Cap prefab = deckEntry.BasePrefab;
            if (prefab == null) continue;

            GameObject entryObj = Instantiate(_capEntryPrefab, _contentParent);
            Image capIcon = entryObj.GetComponent<Image>();
            if (capIcon == null) capIcon = entryObj.GetComponentInChildren<Image>();
            RectTransform capIconRT = capIcon != null ? capIcon.rectTransform : null;

            var entry = new DeckEntry
            {
                Cap = prefab, // store the prefab reference (used for sticker lookup)
                EntryObj = entryObj,
                CapIcon = capIcon,
                CapIconRT = capIconRT,
                StickerImages = new List<Image>(),
                StickerDescriptions = new List<string>()
            };

            // Set the cap icon sprite: prefer the stored GeneratedFaceSprite
            // (from the run deck entry), fall back to the prefab's DeckSprite,
            // then _fallbackCapSprite.
            if (capIcon != null)
            {
                Sprite capSprite = deckEntry.GeneratedFaceSprite;
                if (capSprite == null) capSprite = prefab.DeckSprite;
                if (capSprite == null) capSprite = _fallbackCapSprite;
                if (capSprite != null)
                    capIcon.sprite = capSprite;
            }

            // Spawn sticker images based on the DeckEntry's ability levels.
            // The sticker SPRITES + DESCRIPTIONS come from the deck's ability
            // template prefabs (which have the ability components configured).
            // The LEVELS come from the DeckEntry.
            //
            // For gained enemy caps, BasePrefab is a captured clone that already
            // has ability components — in that case, read directly from the
            // clone's components (they have the correct levels baked in).
            if (_stickerImagePrefab != null)
            {
                // Check if the base prefab already has ability components
                // (e.g., gained enemy cap captured as a clone). If so, read
                // directly from them — the clone's state is authoritative.
                var existingAbilities = prefab.GetComponents<ICapAbility>();
                if (existingAbilities != null && existingAbilities.Length > 0)
                {
                    // Gained enemy cap (captured clone) — read from its components.
                    AddStickersFromAbilities(existingAbilities, entryObj, entry);
                }
                else
                {
                    // Player cap from the deck (base prefab, no abilities) —
                    // read stickers from the deck's ability templates, using
                    // the DeckEntry's level fields.
                    AddStickersFromDeckEntry(deckEntry, deckAsset, entryObj, entry);
                }
            }

            _entries.Add(entry);
        }
    }

    /// <summary>
    /// Adds sticker Images for each ICapAbility in the given list. Used for
    /// gained enemy caps (captured clones) that already have ability components
    /// — reads the sticker sprite + description + level directly from each
    /// component.
    /// </summary>
    void AddStickersFromAbilities(ICapAbility[] abilities, GameObject entryObj, DeckEntry entry)
    {
        for (int a = 0; a < abilities.Length; a++)
        {
            if (abilities[a] == null) continue;
            Sprite stickerSprite = abilities[a].StickerSprite;
            if (stickerSprite == null) continue;

            AddStickerImage(stickerSprite, abilities[a].Description, abilities[a].Level,
                entryObj, entry);
        }
    }

    /// <summary>
    /// Adds sticker Images based on a DeckEntry's ability levels. Used for
    /// player caps from the deck (base prefab has no ability components).
    /// Reads the sticker sprite + description from the deck's ability template
    /// prefabs, and the level from the DeckEntry.
    /// </summary>
    void AddStickersFromDeckEntry(RunManager.DeckEntry deckEntry, CapDeckDefinition deck,
        GameObject entryObj, DeckEntry entry)
    {
        if (deck == null) return;

        // Bomb
        if (deckEntry.BombLevel > 0 && deck.BombTemplate != null)
        {
            var ability = deck.BombTemplate.GetComponent<BombCapFlipEffect>();
            if (ability != null && ability.StickerSprite != null)
                AddStickerImage(ability.StickerSprite, ability.Description, deckEntry.BombLevel,
                    entryObj, entry);
        }

        // Flipper
        if (deckEntry.FlipperLevel > 0 && deck.FlipperTemplate != null)
        {
            var ability = deck.FlipperTemplate.GetComponent<FlipperCapEffect>();
            if (ability != null && ability.StickerSprite != null)
                AddStickerImage(ability.StickerSprite, ability.Description, deckEntry.FlipperLevel,
                    entryObj, entry);
        }

        // Defender
        if (deckEntry.DefenderLevel > 0 && deck.DefenderTemplate != null)
        {
            var ability = deck.DefenderTemplate.GetComponent<DefenderCapEffect>();
            if (ability != null && ability.StickerSprite != null)
                AddStickerImage(ability.StickerSprite, ability.Description, deckEntry.DefenderLevel,
                    entryObj, entry);
        }

        // Predictor
        if (deckEntry.PredictorLevel > 0 && deck.PredictorTemplate != null)
        {
            var ability = deck.PredictorTemplate.GetComponent<PredictorCapEffect>();
            if (ability != null && ability.StickerSprite != null)
                AddStickerImage(ability.StickerSprite, ability.Description, deckEntry.PredictorLevel,
                    entryObj, entry);
        }
    }

    /// <summary>
    /// Creates a sticker Image, sets its sprite + size + level badge, and adds
    /// it to the entry's StickerImages + StickerDescriptions lists.
    /// </summary>
    void AddStickerImage(Sprite stickerSprite, string description, int level,
        GameObject entryObj, DeckEntry entry)
    {
        GameObject stickerObj = Instantiate(_stickerImagePrefab, entryObj.transform);
        Image stickerImg = stickerObj.GetComponent<Image>();
        if (stickerImg == null) stickerImg = stickerObj.GetComponentInChildren<Image>();
        if (stickerImg == null) stickerImg = stickerObj.AddComponent<Image>();
        stickerImg.sprite = stickerSprite;
        stickerImg.preserveAspect = true;
        stickerImg.raycastTarget = false;

        RectTransform srt = stickerObj.transform as RectTransform;
        if (srt != null)
            srt.sizeDelta = _stickerSize;
        stickerObj.SetActive(true);

        StickerView view = stickerObj.GetComponent<StickerView>();
        if (view == null)
            view = stickerObj.AddComponent<StickerView>();
        view.SetLevel(level);

        entry.StickerImages.Add(stickerImg);
        entry.StickerDescriptions.Add(description ?? string.Empty);
    }

    /// <summary>
    /// Returns the current deck count, branching by _source.
    /// </summary>
    int CurrentDeckCount
    {
        get
        {
            if (_source == Source.RunManager)
            {
                RunManager rm = RunManager.Instance;
                return rm != null && rm.RunDeck != null ? rm.RunDeck.Count : 0;
            }
            return _capHand != null ? _capHand.DeckCount : 0;
        }
    }

    /// <summary>
    /// Per-frame: positions each sticker radially around its cap icon, using
    /// the same angle-step logic as StickerManager but on a flat UI plane.
    /// Sticker i sits at angle (i * _stickerAngleStep) degrees counter-clockwise
    /// from "up" (top of screen), at radius _stickerRadiusPixels from the cap
    /// icon's center.
    ///
    /// This must run every frame because the cap icon's screen position can
    /// shift as the layout settles, the panel scrolls, or the window resizes.
    /// </summary>
    void UpdateStickerPositions()
    {
        Camera uiCamera = (_uiCanvas != null && _uiCanvas.renderMode == RenderMode.ScreenSpaceCamera)
            ? _uiCanvas.worldCamera
            : null;

        for (int e = 0; e < _entries.Count; e++)
        {
            DeckEntry entry = _entries[e];
            if (entry.CapIconRT == null || entry.StickerImages == null) continue;

            // The cap icon's center in screen pixels.
            Vector3 capIconScreenCenter = RectTransformUtility.WorldToScreenPoint(uiCamera, entry.CapIconRT.position);

            // The entry's root RectTransform — stickers are positioned relative
            // to it (their parent). Compute its screen center so we can convert
            // the radial offset into local coordinates.
            RectTransform entryRT = entry.EntryObj.transform as RectTransform;
            if (entryRT == null) continue;
            Vector3 entryScreenCenter = RectTransformUtility.WorldToScreenPoint(uiCamera, entryRT.position);

            // Convert both screen points to local coordinates inside the entry.
            // The sticker's local position = (capIconLocal - entryLocal) + radial offset.
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                entryRT, capIconScreenCenter, uiCamera, out Vector2 capIconLocal);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                entryRT, entryScreenCenter, uiCamera, out Vector2 entryLocal);
            Vector2 capIconOffset = capIconLocal - entryLocal;

            int stickerCount = entry.StickerImages.Count;
            for (int s = 0; s < stickerCount; s++)
            {
                Image stickerImg = entry.StickerImages[s];
                if (stickerImg == null) continue;
                RectTransform srt = stickerImg.rectTransform;

                // Angle: first sticker at 0° (top), then counter-clockwise.
                // Matches StickerManager: dir = (up * cos - right * sin).
                // On a flat UI plane, "up" is +Y and "right" is +X, so:
                //   dir = (cos(angle), sin(angle)) but with Y-up convention:
                //   dir = (sin(angle) for X, cos(angle) for Y) if we want 0° = top.
                // StickerManager uses 0° = camUpFlat (top), counter-clockwise.
                // In screen space with Y-up: 0° = (0, +1), 90° = (-1, 0), etc.
                float angleDeg = s * _stickerAngleStep;
                float angleRad = angleDeg * Mathf.Deg2Rad;
                Vector2 dir = new Vector2(-Mathf.Sin(angleRad), Mathf.Cos(angleRad));

                Vector2 stickerLocal = capIconOffset + dir * _stickerRadiusPixels;
                srt.anchoredPosition = stickerLocal;
            }
        }
    }

    /// <summary>
    /// Per-frame: checks if the cursor is over any sticker in any deck entry.
    /// If so, shows the tooltip with that sticker's description. Else hides it.
    /// </summary>
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

        // Precompute the hover-radius scale factor from the sticker's visual size.
        // Mirrors StickerManager's `hoverRadius = _stickerHoverRadius * StickerVisualScale`.
        // Here the "visual scale" is the sticker's average dimension / 32, so a
        // 32x32 sticker uses the base radius, a 64x64 sticker uses 2x, etc.
        float stickerVisualScale = ((_stickerSize.x + _stickerSize.y) * 0.5f) / 32f;
        float scaledHoverRadius = _stickerHoverRadius * stickerVisualScale;

        for (int e = 0; e < _entries.Count && !foundHover; e++)
        {
            DeckEntry entry = _entries[e];
            if (entry.StickerImages == null) continue;

            for (int s = 0; s < entry.StickerImages.Count; s++)
            {
                Image sticker = entry.StickerImages[s];
                if (sticker == null) continue;

                // Update the sticker's screen position (in case the layout moved).
                RectTransform stickerRT = sticker.rectTransform;
                Vector3 stickerWorldPos = stickerRT.position;
                Vector3 stickerScreenPos = RectTransformUtility.WorldToScreenPoint(uiCamera, stickerWorldPos);

                float dist = Vector2.Distance(mousePos, new Vector2(stickerScreenPos.x, stickerScreenPos.y));
                if (dist <= scaledHoverRadius)
                {
                    foundHover = true;
                    hoveredStickerScreenPos = new Vector2(stickerScreenPos.x, stickerScreenPos.y);
                    if (s < entry.StickerDescriptions.Count)
                        hoveredDescription = entry.StickerDescriptions[s];
                    break;
                }
            }
        }

        if (foundHover)
            ShowTooltip(hoveredDescription, hoveredStickerScreenPos);
        else
            HideTooltip();
    }

    void ShowTooltip(string text, Vector2 stickerScreenPos)
    {
        if (_tooltipInstance == null || _tooltipRect == null) return;
        if (string.IsNullOrEmpty(text)) return;

        _tooltipInstance.SetActive(true);
        if (_hintView != null)
            _hintView.SetText(text);

        // Force layout rebuild so we have accurate dimensions.
        UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(_tooltipRect);

        Vector2 tooltipSize = _tooltipRect.rect.size * _tooltipRect.lossyScale.x;
        float sw = Screen.width;
        float sh = Screen.height;

        Camera uiCamera = (_uiCanvas != null && _uiCanvas.renderMode == RenderMode.ScreenSpaceCamera)
            ? _uiCanvas.worldCamera
            : null;

        // Same logic as StickerManager.ShowTooltip: try the offset, if it goes
        // offscreen try the negated offset. If both go offscreen (e.g., the
        // tooltip is too wide to fit beside a sticker near the right edge),
        // use the negated offset (below the sticker — which usually fits Y-wise)
        // and clamp X so the tooltip fits horizontally. This preserves the
        // "try offset, try negated" pattern while handling horizontal overflow.
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

        // Both offsets went offscreen. Use the negated offset (below the sticker)
        // and clamp X so the tooltip fits horizontally. Y is left as-is (below
        // the sticker) — this matches the "invert the offset" behavior the user
        // requested. Only X is clamped, so the tooltip doesn't overlap the sticker
        // vertically.
        {
            Vector2 target = stickerScreenPos - _hintOffset;
            // Clamp X so the tooltip stays fully on screen horizontally.
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

    void UpdateCountText()
    {
        if (_countText == null) return;
        int count = CurrentDeckCount;
        _countText.text = count < 10 ? $"{count}" : $"{count}";
    }
}
