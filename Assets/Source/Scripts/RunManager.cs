using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Persistent singleton that manages the entire run state.
/// Created when the player presses "START GAME" in the main menu.
/// Survives scene transitions via DontDestroyOnLoad.
/// Destroyed when the run ends (all hearts used or all levels cleared).
///
/// Responsibilities:
///   - Holds the player's live deck (caps with pre-generated visuals).
///   - Tracks hearts (restarts remaining).
///   - Tracks current level index.
///   - Handles cap loss/gain after each battle.
///   - Handles boss-level deck snapshots (restore on boss loss).
///   - Loads the next scene on win / restart on loss / returns to menu on run over.
///
/// Battle result reporting:
///   OnBattleEnded carries a <see cref="BattleResult"/> with immutable snapshots
///   of caps lost/gained. The snapshots are taken IMMEDIATELY when a cap leaves
///   the field (in <see cref="RecordCapLost"/>), BEFORE the cap GameObject is
///   destroyed by FallingCap.HandleVanish. This way the snapshot data (sprites,
///   sticker descriptions) remains valid even after the original cap is gone.
/// </summary>
public class RunManager : MonoBehaviour
{
    public static RunManager Instance { get; private set; }

    [Header("Config")]
    [Tooltip("The LevelSequence asset that defines the run's levels and starting deck.")]
    [SerializeField] private LevelSequence _levelSequence;

    [Header("Debug")]
    [Tooltip("If true, logs run state transitions to the console.")]
    [SerializeField] private bool _logRun = true;

    // --- Run state ---

    /// <summary>
    /// A single entry in the run deck. Stores the composed cap definition
    /// (base prefab + ability levels) + generated sprites + a unique EntryId
    /// for loss matching.
    ///
    /// For gained enemy caps, the BasePrefab is the captured clone (an exact
    /// copy of the enemy cap at capture time). The ability levels are read
    /// off the clone's components.
    /// </summary>
    [System.Serializable]
    public class DeckEntry
    {
        /// <summary>
        /// Unique ID for this entry. Stamped onto each Cap created from this
        /// entry (via Cap.RunDeckEntryId). Used to identify which deck entry a
        /// lost cap came from — no sprite matching needed.
        /// </summary>
        public int EntryId;

        /// <summary>
        /// The base cap prefab. For player caps from the deck, this is the
        /// visual-only base prefab. For gained enemy caps, this is the captured
        /// clone (an exact copy of the enemy cap — preserves ability components
        /// and their current levels).
        /// </summary>
        public Cap BasePrefab;

        /// <summary>Ability levels (0 = no ability, 1-3 = ability level).</summary>
        public int BombLevel;
        public int FlipperLevel;
        public int DefenderLevel;
        public int PredictorLevel;

        public Sprite GeneratedFaceSprite;
        public Sprite GeneratedBackSprite;
        public CapOwner OriginalOwner = CapOwner.Player;

        public DeckEntry(int entryId, Cap basePrefab, int bomb, int flipper, int defender, int predictor,
            Sprite face, Sprite back, CapOwner owner)
        {
            EntryId = entryId;
            BasePrefab = basePrefab;
            BombLevel = bomb;
            FlipperLevel = flipper;
            DefenderLevel = defender;
            PredictorLevel = predictor;
            GeneratedFaceSprite = face;
            GeneratedBackSprite = back;
            OriginalOwner = owner;
        }
    }

    /// <summary>The player's live deck for this run. Modified as caps are lost/gained.</summary>
    public List<DeckEntry> RunDeck { get; private set; } = new();

    /// <summary>Current level index (0-based).</summary>
    public int CurrentLevelIndex { get; private set; }

    /// <summary>Hearts remaining (restarts left).</summary>
    public int Hearts { get; private set; }

    /// <summary>True if the run is currently active.</summary>
    public bool IsRunActive { get; private set; }

    // --- Boss snapshot ---
    // Before a boss fight, the deck is snapshotted. If the player loses the boss,
    // the deck is restored to this snapshot (caps lost during the attempt come back,
    // caps gained during the attempt are removed).
    private List<DeckEntry> _bossDeckSnapshot;
    private bool _isBossLevel;

    // --- Caps lost/gained tracking during the current battle ---
    // We store BOTH the snapshot (for the rewards UI) AND the live Cap reference
    // (for CommitCapChanges, which needs the prefab / GeneratedFaceSprite to
    // modify the deck). The Cap reference may become null if the cap is
    // destroyed by FallingCap.HandleVanish before CommitCapChanges runs — in
    // that case, CommitCapChanges falls back to the CapturedClone.
    //
    // CapturedClone: for ENEMY caps, we CLONE the cap at the moment it leaves
    // the field (before FallingCap destroys it) and park the clone under
    // RunManager (which survives scene transitions via DontDestroyOnLoad). The
    // clone preserves the cap's full live state — materials, ability levels,
    // sticker data — so when re-instantiated on the next level, the player gets
    // the EXACT cap they killed, not a stale prefab reference. This is the fix
    // for "caps will have changes made to them during play" — the clone reflects
    // the cap's current state, not the original prefab's state.
    struct LostCapRecord
    {
        public CapSnapshot Snapshot;
        public Cap Cap; // may be null if destroyed by FallingCap
        public Cap CapturedClone; // for enemy caps: a parked clone that survives scene unload
        public int RunDeckEntryId; // for player caps: the RunDeck entry ID to remove
    }

    private readonly List<LostCapRecord> _playerCapsLostThisBattle = new();
    private readonly List<LostCapRecord> _enemyCapsLostThisBattle = new();

    /// <summary>
    /// Hidden container (child of RunManager) where captured enemy cap clones
    /// are parked. Because RunManager is DontDestroyOnLoad, the clones survive
    /// scene transitions and can be used as the source for CapFactory.Create on
    /// the next level. Lazily created on first use.
    /// </summary>
    private Transform _capturedCapsContainer;

    // --- Events ---
    /// <summary>Raised when the run starts (before the first scene loads).</summary>
    public event System.Action OnRunStarted;

    /// <summary>
    /// Raised when a battle ends. Carries a <see cref="BattleResult"/> with
    /// immutable cap snapshots for the rewards UI. The cap snapshots are safe
    /// to hold across frames — they don't depend on the lifetime of the
    /// original Cap GameObjects.
    /// </summary>
    public event System.Action<BattleResult> OnBattleEnded;

    /// <summary>Raised when hearts change. (newHearts)</summary>
    public event System.Action<int> OnHeartsChanged;

    /// <summary>Raised when the run ends (all hearts used or all levels cleared).</summary>
    public event System.Action<bool> OnRunEnded; // bool = isVictory (true = all levels cleared)

    /// <summary>
    /// The last <see cref="BattleResult"/> reported. Useful for UI that activates
    /// after the event has fired (e.g., a panel that's enabled by a button click
    /// and needs to read the result retroactively).
    /// </summary>
    public BattleResult LastBattleResult { get; private set; }

    /// <summary>
    /// Clears <see cref="LastBattleResult"/>. Called by BattleResultUI when the
    /// player dismisses the post-battle panel (Next / Continue / Retry / Return).
    /// Without this, the NEXT scene's MatchRewardsPanel would see the previous
    /// battle's result (RunManager persists via DontDestroyOnLoad) and might
    /// auto-show the panel — making it look like the result panel "didn't hide
    /// itself" on the next level.
    /// </summary>
    public void ClearLastBattleResult()
    {
        LastBattleResult = null;
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;

        // Clean up any remaining captured cap clones. When the run ends,
        // RunManager is destroyed — any clones parked under it should be
        // destroyed too (they're not in RunDeck, so CleanupUncapturedClones
        // wouldn't catch them if OnDestroy runs before cleanup).
        if (_capturedCapsContainer != null)
        {
            // Destroy the container — this destroys all its children (the clones).
            Destroy(_capturedCapsContainer.gameObject);
            _capturedCapsContainer = null;
        }
    }

    // -----------------------------------------------------------------------
    // Run lifecycle
    // -----------------------------------------------------------------------

    /// <summary>
    /// Starts a new run. Generates the deck from the LevelSequence's starting
    /// deck template, sets hearts, and loads the first level.
    /// </summary>
    public void StartRun(LevelSequence sequence)
    {
        _levelSequence = sequence;
        CurrentLevelIndex = 0;
        Hearts = sequence.StartingHearts;
        IsRunActive = true;
        RunDeck.Clear();
        _playerCapsLostThisBattle.Clear();
        _enemyCapsLostThisBattle.Clear();
        LastBattleResult = null;

        // Generate the deck from the composed cap entries. Each entry specifies
        // a base prefab + ability levels. We generate visuals ONCE (face/back
        // sprites) and store the composed entry for later re-instantiation.
        if (sequence.StartingPlayerDeck != null && sequence.StartingPlayerDeck.Caps != null)
        {
            for (int i = 0; i < sequence.StartingPlayerDeck.Caps.Length; i++)
            {
                CapDeckDefinition.ComposedCapEntry entry = sequence.StartingPlayerDeck.Caps[i];
                if (entry.BasePrefab == null) continue;
                RunDeck.Add(GenerateDeckEntry(entry, sequence.StartingPlayerDeck, CapOwner.Player));
            }
        }

        if (_logRun)
            Debug.Log($"[RunManager] Run started. Deck: {RunDeck.Count} caps. Hearts: {Hearts}. First level: {GetLevelSceneName(0)}");

        OnRunStarted?.Invoke();
        LoadLevel(0);
    }

    /// <summary>Ends the run and returns to the main menu.</summary>
    public void EndRun(bool isVictory)
    {
        IsRunActive = false;
        if (_logRun)
            Debug.Log($"[RunManager] Run ended. Victory: {isVictory}. Hearts: {Hearts}");
        OnRunEnded?.Invoke(isVictory);
    }

    // -----------------------------------------------------------------------
    // Battle result handling
    // -----------------------------------------------------------------------

    /// <summary>
    /// Called by TurnController when a match finishes. Processes cap loss/gain,
    /// handles boss snapshots, and triggers the next scene load.
    /// </summary>
    public void OnMatchFinished(CapOwner winner, MatchEndReason reason)
    {
        if (!IsRunActive) return;

        bool playerWon = winner == CapOwner.Player;
        _isBossLevel = IsBossLevel(CurrentLevelIndex);

        if (_logRun)
            Debug.Log($"[RunManager] Battle ended. Winner: {winner}. Boss: {_isBossLevel}. Reason: {reason}. Player lost: {_playerCapsLostThisBattle.Count}, Enemy lost: {_enemyCapsLostThisBattle.Count}");

        // --- Build the BattleResult BEFORE committing changes / clearing lists ---
        // The cap snapshots were already taken in RecordCapLost (when each cap
        // left the field), so they're valid even if the cap GameObjects have
        // since been destroyed by FallingCap.HandleVanish.
        BattleResult result = BuildBattleResult(winner, reason, _isBossLevel);
        LastBattleResult = result;

        // --- Process caps lost/gained ---
        if (!_isBossLevel || playerWon)
        {
            // Non-boss OR boss win: commit the changes.
            CommitCapChanges();
        }
        else
        {
            // Boss loss: restore the deck to the snapshot (undo this attempt's changes).
            if (_bossDeckSnapshot != null)
            {
                RunDeck = new List<DeckEntry>(_bossDeckSnapshot);
                if (_logRun)
                    Debug.Log("[RunManager] Boss lost — deck restored to snapshot.");
            }
        }

        // --- Hearts ---
        if (!playerWon)
        {
            Hearts = Mathf.Max(0, Hearts - 1);
            OnHeartsChanged?.Invoke(Hearts);
        }
        result.HeartsRemaining = Hearts;

        // --- Determine next action ---
        if (playerWon)
        {
            int nextLevel = CurrentLevelIndex + 1;
            if (nextLevel >= _levelSequence.Levels.Length)
            {
                OnBattleEnded?.Invoke(result);
                EndRun(true);
            }
            else
            {
                OnBattleEnded?.Invoke(result);
            }
        }
        else
        {
            if (Hearts <= 0)
            {
                OnBattleEnded?.Invoke(result);
                EndRun(false);
            }
            else if (_isBossLevel)
            {
                OnBattleEnded?.Invoke(result);
            }
            else
            {
                int nextLevel = CurrentLevelIndex + 1;
                if (nextLevel >= _levelSequence.Levels.Length)
                {
                    OnBattleEnded?.Invoke(result);
                    EndRun(false);
                }
                else
                {
                    OnBattleEnded?.Invoke(result);
                }
            }
        }

        // Clear the tracking lists. Captured clones that were added to RunDeck
        // (via CommitCapChanges) persist — they're the prefab source for the next
        // level. Captured clones that were NOT added (e.g., boss loss → deck
        // restored to snapshot) are destroyed here to avoid a memory leak.
        CleanupUncapturedClones();
        _playerCapsLostThisBattle.Clear();
        _enemyCapsLostThisBattle.Clear();
    }

    /// <summary>
    /// Destroys captured clones that were NOT committed to RunDeck. Called after
    /// OnMatchFinished processes the battle result. Clones that WERE committed
    /// (added to RunDeck) are kept — they're the prefab source for the next level.
    ///
    /// This handles the boss-loss case: the deck is restored to the snapshot, so
    /// any captured enemy clones from this attempt are not added to RunDeck and
    /// must be destroyed to avoid orphaned GameObjects under RunManager.
    /// </summary>
    void CleanupUncapturedClones()
    {
        // Build a set of all cap references currently in RunDeck (these are the
        // committed clones that should be kept). Anything in the lost-cap records
        // that's NOT in this set should be destroyed.
        var committedClones = new HashSet<Cap>();
        for (int i = 0; i < RunDeck.Count; i++)
        {
            if (RunDeck[i].BasePrefab != null)
                committedClones.Add(RunDeck[i].BasePrefab);
        }

        // Check enemy lost records (player lost records don't have captured clones).
        for (int i = 0; i < _enemyCapsLostThisBattle.Count; i++)
        {
            LostCapRecord record = _enemyCapsLostThisBattle[i];
            Cap clone = record.CapturedClone;
            if (clone == null) continue;
            // Unity's overloaded == returns true for destroyed objects, so this
            // check also catches clones that were already destroyed.
            if (!committedClones.Contains(clone))
            {
                if (_logRun)
                    Debug.Log($"[RunManager] Cleaning up uncommitted clone: {clone.name}");
                Destroy(clone.gameObject);
            }
        }
    }

    /// <summary>
    /// Builds a <see cref="BattleResult"/> from the current battle state.
    /// Uses the snapshots that were already taken in RecordCapLost (when each
    /// cap left the field). The snapshots are valid even if the cap GameObjects
    /// have been destroyed since.
    /// </summary>
    BattleResult BuildBattleResult(CapOwner winner, MatchEndReason reason, bool isBoss)
    {
        var result = new BattleResult
        {
            Winner = winner,
            Reason = reason,
            IsBoss = isBoss,
            HeartsRemaining = Hearts,
            CurrentLevel = CurrentLevelIndex,
            TotalLevels = TotalLevels,
        };

        // Lost player caps — use the pre-built snapshots.
        if (_playerCapsLostThisBattle.Count > 0)
        {
            var lost = new List<CapSnapshot>(_playerCapsLostThisBattle.Count);
            for (int i = 0; i < _playerCapsLostThisBattle.Count; i++)
                lost.Add(_playerCapsLostThisBattle[i].Snapshot);
            result.LostCaps = lost;
        }

        // Gained enemy caps — use the pre-built snapshots.
        if (_enemyCapsLostThisBattle.Count > 0)
        {
            var gained = new List<CapSnapshot>(_enemyCapsLostThisBattle.Count);
            for (int i = 0; i < _enemyCapsLostThisBattle.Count; i++)
                gained.Add(_enemyCapsLostThisBattle[i].Snapshot);
            result.GainedCaps = gained;
        }

        if (_logRun)
            Debug.Log($"[RunManager] Built BattleResult: lost {result.LostCaps.Count}, gained {result.GainedCaps.Count}.");

        return result;
    }

    /// <summary>
    /// Builds an immutable <see cref="CapSnapshot"/> from a live Cap instance.
    /// Captures the cap icon (DeckSprite — preferred: GeneratedFaceSprite),
    /// back sprite, and all sticker info. Safe to hold after the cap is destroyed.
    ///
    /// IMPORTANT: this must be called while the cap is STILL ALIVE (i.e., in
    /// RecordCapLost, which fires when the cap leaves the field but BEFORE
    /// FallingCap.HandleVanish destroys it). If called on a destroyed cap,
    /// GetComponent/GetComponents return null/empty.
    /// </summary>
    CapSnapshot BuildCapSnapshot(Cap cap)
    {
        if (cap == null) return default;

        Sprite iconSprite = cap.DeckSprite; // returns GeneratedFaceSprite if available
        Sprite backSprite = null;

        var gen = cap.GetComponent<CapVisualGenerator>();
        if (gen != null)
        {
            backSprite = gen.GeneratedBackSprite;
            if (iconSprite == null) iconSprite = backSprite;
        }

        // Stickers: one per ICapAbility on the cap's root (matches StickerManager's behavior).
        List<StickerSnapshot> stickers = null;
        var abilities = cap.GetComponents<ICapAbility>();
        for (int i = 0; i < abilities.Length; i++)
        {
            if (abilities[i] == null) continue;
            Sprite stickerSprite = abilities[i].StickerSprite;
            if (stickerSprite == null) continue;
            if (stickers == null) stickers = new List<StickerSnapshot>(abilities.Length);
            stickers.Add(new StickerSnapshot(
                stickerSprite,
                abilities[i].Description ?? string.Empty,
                abilities[i].Level));
        }

        return new CapSnapshot(iconSprite, backSprite, cap.name,
            stickers != null ? stickers : System.Array.Empty<StickerSnapshot>());
    }

    /// <summary>
    /// Records a cap that was knocked off the field during the current battle.
    /// Called by TurnController (which subscribes to CapFieldBoundary.OnCapLeftField).
    ///
    /// IMPORTANT: this fires when the cap LEAVES the field (CapFieldBoundary.DropCap
    /// raises OnCapLeftField BEFORE handing the cap to FallingCap.Begin). At this
    /// point the cap GameObject is still alive — its components (CapVisualGenerator,
    /// ICapAbility) are still readable. The snapshot is taken NOW so the rewards UI
    /// can display the cap's icon and stickers even after the cap is destroyed by
    /// FallingCap.HandleVanish later.
    ///
    /// For ENEMY caps, we also CLONE the cap NOW and park the clone under
    /// RunManager (DontDestroyOnLoad). The clone preserves the cap's full live
    /// state — materials, ability levels, sticker data — so when re-instantiated
    /// on the next level, the player gets the EXACT cap they killed. This avoids
    /// the stale-prefab problem: looking up the original prefab would lose any
    /// runtime modifications (level ups, stat changes, etc.).
    /// </summary>
    public void RecordCapLost(Cap cap)
    {
        if (cap == null) return;

        // Build the snapshot NOW, while the cap is still alive.
        CapSnapshot snapshot = BuildCapSnapshot(cap);

        // For enemy caps, clone the cap and park it under RunManager so it
        // survives scene transitions. The clone is used as the DeckEntry's
        // "prefab" source on the next level.
        Cap capturedClone = null;
        if (cap.Owner == CapOwner.Opponent)
            capturedClone = CaptureCapClone(cap);

        var record = new LostCapRecord
        {
            Snapshot = snapshot,
            Cap = cap,
            CapturedClone = capturedClone,
            RunDeckEntryId = cap.RunDeckEntryId, // 0 if not from the run deck
        };

        if (cap.Owner == CapOwner.Player)
            _playerCapsLostThisBattle.Add(record);
        else if (cap.Owner == CapOwner.Opponent)
            _enemyCapsLostThisBattle.Add(record);

        if (_logRun)
            Debug.Log($"[RunManager] Recorded cap lost: {cap.name} (owner={cap.Owner}). " +
                      $"Snapshotted {snapshot.Stickers.Count} stickers. " +
                      $"Clone captured: {(capturedClone != null ? "yes" : "no")}. " +
                      $"RunDeckEntryId: {cap.RunDeckEntryId}.");
    }

    /// <summary>
    /// Creates a deep copy of the cap (via Object.Instantiate) and parks it
    /// under RunManager's captured-caps container. The clone preserves the
    /// cap's full live state — cloned materials, ability levels, sticker data —
    /// because Instantiate copies all components and their serialized fields.
    ///
    /// The clone is set inactive so it doesn't render or interact. It's used
    /// ONLY as the source for CapFactory.Create on the next level (which
    /// Instantiate-s a fresh copy from it).
    ///
    /// Called at RecordCapLost time. The original cap has ALREADY started its
    /// fall (FallingCap.Begin ran in CapFieldBoundary.DropCap BEFORE firing
    /// OnCapLeftField). The original continues its fall animation and gets
    /// destroyed; the clone is safely parked under RunManager.
    /// </summary>
    Cap CaptureCapClone(Cap cap)
    {
        if (cap == null) return null;
        try
        {
            Transform container = GetCapturedCapsContainer();
            // Instantiate as a child of the captured-caps container. This keeps
            // the clone under RunManager (DontDestroyOnLoad) so it survives scene
            // transitions. The clone's transform is set to a far-away position
            // as a safety measure (in case it's accidentally activated).
            Cap clone = Object.Instantiate(cap, container);
            clone.gameObject.name = $"Captured_{cap.gameObject.name}";

            // IMPORTANT: the cap was captured at RecordCapLost time, which now
            // runs AFTER FallingCap.Begin (CapFieldBoundary.DropCap starts the
            // fall before firing OnCapLeftField). So the original cap has a
            // FallingCap component added by FallingCap.Begin. Instantiate copies
            // all components, so the clone would ALSO have a FallingCap component.
            // If left in place, the clone's FallingCap.Update would run (if
            // accidentally activated) and try to animate the fall using stale
            // settings — corrupting the clone's state. Worse, when CapFactory.Create
            // instantiates from this clone on the next level, the new cap would
            // inherit the stale FallingCap component. Destroy it here to keep the
            // clone clean.
            FallingCap fallingCap = clone.GetComponent<FallingCap>();
            if (fallingCap != null)
                Object.Destroy(fallingCap);

            // Also re-enable the Cap component (FallingCap.Begin disables it).
            // The clone is set inactive below, but if it's ever activated (e.g.,
            // by CapFactory.Create on the next level), Cap.enabled should be true
            // so the cap can be driven normally.
            clone.enabled = true;

            clone.gameObject.SetActive(false);
            return clone;
        }
        catch (System.Exception e)
        {
            if (_logRun)
                Debug.LogWarning($"[RunManager] Failed to capture clone of {cap.name}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Lazily creates (or returns the existing) hidden container for captured
    /// cap clones. Parented to RunManager's transform so it inherits
    /// DontDestroyOnLoad.
    /// </summary>
    Transform GetCapturedCapsContainer()
    {
        if (_capturedCapsContainer != null) return _capturedCapsContainer;
        var go = new GameObject("CapturedCaps");
        go.transform.SetParent(transform, false);
        _capturedCapsContainer = go.transform;
        return _capturedCapsContainer;
    }

    /// <summary>
    /// Commits the cap changes: removes lost player caps from the run deck,
    /// adds gained enemy caps (with generated visuals).
    ///
    /// For gained enemy caps, we use the CAPTURED CLONE (parked under RunManager
    /// at RecordCapLost time) as the DeckEntry's prefab source — NOT the original
    /// prefab looked up by name, and NOT the live cap instance (which is destroyed
    /// by FallingCap). The clone preserves the cap's full live state (materials,
    /// ability levels, sticker data), so re-instantiation on the next level produces
    /// a cap with the EXACT state the player killed — not a stale prefab reference.
    /// </summary>
    void CommitCapChanges()
    {
        RunDeck = RemoveLostPlayerCaps(RunDeck);

        // Add enemy caps that were knocked off (gained by the player).
        for (int i = 0; i < _enemyCapsLostThisBattle.Count; i++)
        {
            LostCapRecord record = _enemyCapsLostThisBattle[i];
            Cap enemyCap = record.Cap; // may be null if destroyed by FallingCap
            Cap capturedClone = record.CapturedClone; // survives scene unload (parked under RunManager)

            // Prefer the captured clone — it's a deep copy of the enemy cap's
            // state at the moment it was knocked off, and it survives scene
            // transitions because it's parented to RunManager (DontDestroyOnLoad).
            Cap prefab = capturedClone;
            if (prefab == null)
            {
                // Fallback: use the live cap if still alive (works only if the
                // cap hasn't been destroyed yet — unreliable across scenes).
                prefab = enemyCap;
            }

            if (prefab == null)
            {
                if (_logRun)
                    Debug.LogWarning($"[RunManager] Gained enemy cap '{record.Snapshot.DisplayName}' has no captured clone and the live cap is destroyed — can't add to deck (snapshot still shown in rewards UI).");
                continue;
            }

            // Use the STORED sprites (from the snapshot) — these were captured
            // at snapshot time and survive scene transitions. If the live cap
            // is still alive, prefer its current sprites (they should match).
            Sprite faceSprite = record.Snapshot.IconSprite;
            Sprite backSprite = record.Snapshot.BackSprite;
            if (enemyCap != null)
            {
                CapVisualGenerator gen = enemyCap.GetComponent<CapVisualGenerator>();
                if (gen != null)
                {
                    if (gen.GeneratedFaceSprite != null) faceSprite = gen.GeneratedFaceSprite;
                    if (gen.GeneratedBackSprite != null) backSprite = gen.GeneratedBackSprite;
                }
            }

            // Read ability levels off the captured clone (or live cap if available).
            // The clone is an exact copy of the enemy cap, so its ability components
            // and their levels are preserved.
            Cap sourceForLevels = capturedClone != null ? capturedClone : enemyCap;
            int bombLevel = 0, flipperLevel = 0, defenderLevel = 0, predictorLevel = 0;
            if (sourceForLevels != null)
            {
                var bomb = sourceForLevels.GetComponent<BombCapFlipEffect>();
                if (bomb != null) bombLevel = bomb.Level;
                var flipper = sourceForLevels.GetComponent<FlipperCapEffect>();
                if (flipper != null) flipperLevel = flipper.Level;
                var defender = sourceForLevels.GetComponent<DefenderCapEffect>();
                if (defender != null) defenderLevel = defender.Level;
                var predictor = sourceForLevels.GetComponent<PredictorCapEffect>();
                if (predictor != null) predictorLevel = predictor.Level;
            }

            int entryId = CapFactory.NextRunDeckEntryId();
            RunDeck.Add(new DeckEntry(entryId, prefab,
                bombLevel, flipperLevel, defenderLevel, predictorLevel,
                faceSprite, backSprite, CapOwner.Player));

            if (_logRun)
                Debug.Log($"[RunManager] Gained enemy cap: {record.Snapshot.DisplayName} " +
                          $"(source: {(capturedClone != null ? "captured clone" : "live cap")}), " +
                          $"abilities: B={bombLevel} F={flipperLevel} D={defenderLevel} P={predictorLevel}.");
        }
    }

    /// <summary>
    /// Removes lost player caps from the deck by their RunDeckEntryId.
    /// This replaces the old fragile GeneratedFaceSprite matching — now each
    /// cap is stamped with a unique entry ID at creation time, and we just
    /// remove the entry with that ID. Works even if multiple caps have the
    /// same abilities and levels (each has a unique ID).
    /// </summary>
    List<DeckEntry> RemoveLostPlayerCaps(List<DeckEntry> deck)
    {
        var result = new List<DeckEntry>(deck);

        for (int i = 0; i < _playerCapsLostThisBattle.Count; i++)
        {
            LostCapRecord record = _playerCapsLostThisBattle[i];
            int lostEntryId = record.RunDeckEntryId;

            if (lostEntryId == 0)
            {
                // Cap was not from the run deck (e.g., a scene-placed cap, or
                // a cap created in sandbox mode). Skip — nothing to remove.
                if (_logRun)
                    Debug.LogWarning($"[RunManager] Lost cap '{record.Snapshot.DisplayName}' has RunDeckEntryId=0 — not from the run deck, skipping removal.");
                continue;
            }

            // Find and remove the entry with this ID.
            bool found = false;
            for (int j = 0; j < result.Count; j++)
            {
                if (result[j].EntryId == lostEntryId)
                {
                    if (_logRun)
                        Debug.Log($"[RunManager] Removing lost cap entry {lostEntryId} ({result[j].BasePrefab?.name}).");
                    result.RemoveAt(j);
                    found = true;
                    break;
                }
            }

            if (!found && _logRun)
                Debug.LogWarning($"[RunManager] Lost cap entry {lostEntryId} not found in run deck — already removed?");
        }

        return result;
    }

    // -----------------------------------------------------------------------
    // Boss snapshot
    // -----------------------------------------------------------------------

    /// <summary>
    /// Call this BEFORE a boss battle starts. Snapshots the current deck so
    /// it can be restored if the player loses the boss.
    /// </summary>
    public void SnapshotDeckForBoss()
    {
        _bossDeckSnapshot = new List<DeckEntry>(RunDeck);
        if (_logRun)
            Debug.Log($"[RunManager] Boss deck snapshot taken: {RunDeck.Count} caps.");
    }

    // -----------------------------------------------------------------------
    // Scene management
    // -----------------------------------------------------------------------

    public void AdvanceToNextLevel()
    {
        int next = CurrentLevelIndex + 1;
        if (next >= _levelSequence.Levels.Length)
        {
            EndRun(true);
            return;
        }
        LoadLevel(next);
    }

    public void RestartCurrentLevel()
    {
        LoadLevel(CurrentLevelIndex);
    }

    void LoadLevel(int index)
    {
        CurrentLevelIndex = index;
        string sceneName = GetLevelSceneName(index);
        _isBossLevel = IsBossLevel(index);

        if (_isBossLevel)
            SnapshotDeckForBoss();

        if (_logRun)
            Debug.Log($"[RunManager] Loading level {index}: {sceneName} (Boss: {_isBossLevel})");

        if (!string.IsNullOrEmpty(sceneName))
            SceneManager.LoadScene(sceneName);
    }

    // -----------------------------------------------------------------------
    // Queries
    // -----------------------------------------------------------------------

    public string GetLevelSceneName(int index)
    {
        if (_levelSequence == null || _levelSequence.Levels == null) return null;
        if (index < 0 || index >= _levelSequence.Levels.Length) return null;
        return _levelSequence.Levels[index].SceneName;
    }

    public bool IsBossLevel(int index)
    {
        if (_levelSequence == null || _levelSequence.Levels == null) return false;
        if (index < 0 || index >= _levelSequence.Levels.Length) return false;
        return _levelSequence.Levels[index].IsBoss;
    }

    public CapDeckDefinition GetEnemyDeckForLevel(int index)
    {
        if (_levelSequence == null || _levelSequence.Levels == null) return null;
        if (index < 0 || index >= _levelSequence.Levels.Length) return null;
        return _levelSequence.Levels[index].EnemyDeck;
    }

    public LevelSequence LevelSequence => _levelSequence;

    public int TotalLevels => _levelSequence != null && _levelSequence.Levels != null
        ? _levelSequence.Levels.Length : 0;

    // -----------------------------------------------------------------------
    // Deck entry generation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Creates a DeckEntry from a composed cap entry. Instantiates a hidden
    /// preview, calls Configure (which triggers GenerateVisuals for the face/back
    /// sprites), extracts the generated sprites, destroys the preview, and
    /// returns the entry.
    /// </summary>
    DeckEntry GenerateDeckEntry(CapDeckDefinition.ComposedCapEntry entry, CapDeckDefinition deck, CapOwner owner)
    {
        if (entry.BasePrefab == null) return null;

        // Create a hidden instance to generate visuals.
        GameObject previewObj = Instantiate(entry.BasePrefab.gameObject, new Vector3(9999, 9999, 9999), Quaternion.identity);
        previewObj.SetActive(false);
        Cap previewCap = previewObj.GetComponent<Cap>();
        if (previewCap == null)
            previewCap = previewObj.AddComponent<Cap>();

        previewCap.Configure(0, true, owner);

        // Extract generated sprites.
        CapVisualGenerator gen = previewCap.GetComponent<CapVisualGenerator>();
        Sprite faceSprite = gen != null ? gen.GeneratedFaceSprite : null;
        Sprite backSprite = gen != null ? gen.GeneratedBackSprite : null;

        Destroy(previewObj);

        int entryId = CapFactory.NextRunDeckEntryId();
        return new DeckEntry(entryId, entry.BasePrefab,
            entry.BombLevel, entry.FlipperLevel, entry.DefenderLevel, entry.PredictorLevel,
            faceSprite, backSprite, owner);
    }

    /// <summary>
    /// Generates a DeckEntry from a cap that was already instantiated (e.g.,
    /// an enemy cap knocked off the field — captured as a clone). The clone
    /// is used as the BasePrefab (preserves all ability components + levels).
    /// Ability levels are read off the clone's components.
    /// </summary>
    public DeckEntry GenerateDeckEntryFromCap(Cap cap, CapOwner newOwner)
    {
        if (cap == null) return null;

        CapVisualGenerator gen = cap.GetComponent<CapVisualGenerator>();
        Sprite face = gen != null ? gen.GeneratedFaceSprite : null;
        Sprite back = gen != null ? gen.GeneratedBackSprite : null;

        // Read ability levels off the cap's existing components.
        int bombLevel = 0, flipperLevel = 0, defenderLevel = 0, predictorLevel = 0;
        var bomb = cap.GetComponent<BombCapFlipEffect>();
        if (bomb != null) bombLevel = bomb.Level;
        var flipper = cap.GetComponent<FlipperCapEffect>();
        if (flipper != null) flipperLevel = flipper.Level;
        var defender = cap.GetComponent<DefenderCapEffect>();
        if (defender != null) defenderLevel = defender.Level;
        var predictor = cap.GetComponent<PredictorCapEffect>();
        if (predictor != null) predictorLevel = predictor.Level;

        int entryId = CapFactory.NextRunDeckEntryId();
        return new DeckEntry(entryId, cap, bombLevel, flipperLevel, defenderLevel, predictorLevel,
            face, back, newOwner);
    }
}
