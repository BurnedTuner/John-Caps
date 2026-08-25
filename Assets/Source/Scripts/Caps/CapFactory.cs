using UnityEngine;

/// <summary>
/// Instantiates a Cap prefab with correct registry hookup.
/// Radius and materials come from the cap's own CapParameters.
///
/// Two creation paths:
///   - Create(prefab, ...): legacy, instantiates a cap prefab as-is (abilities
///     baked into the prefab). Used for scene-placed caps, sandbox mode, and
///     backward compatibility.
///   - CreateComposed(entry, deck, ...): new, instantiates a base cap prefab
///     then dynamically adds ability components (Bomb/Flipper/Defender/Predictor)
///     based on the deck entry's level sliders. Ability parameters are copied
///     from the deck's template prefabs. Used for run-mode player + enemy caps.
/// </summary>
public static class CapFactory
{
    static int _nextStableId = 1;

    // Counter for run deck entry IDs. Incremented each time a cap is created
    // from a run deck entry (CreateComposed) or when a gained cap is added to
    // the run deck. Used by RunManager to identify which deck entry a lost cap
    // came from — replacing the fragile GeneratedFaceSprite matching.
    static int _nextRunDeckEntryId = 1;

    public static Cap Create(
        Cap prefab,
        Vector2 groundPosition,
        bool isFace,
        CapOwner owner = CapOwner.Neutral)
    {
        if (prefab == null)
        {
            Debug.LogError("[CapFactory] Prefab is null.");
            return null;
        }

        Vector3 worldPos = CapMath.FromXZ(groundPosition, 0f);
        Cap instance = Object.Instantiate(prefab, worldPos, Quaternion.identity);

        Vector3 p = instance.transform.position;
        p.y = 0.05f;
        instance.transform.position = p;

        Cap cap = instance.GetComponent<Cap>();
        if (cap == null) cap = instance.gameObject.AddComponent<Cap>();

        cap.Configure(_nextStableId++, isFace, owner);

        // Awake ran during Instantiate and may have set _isScenePlaced = true
        // (it checks _stableId == 0, but Configure hadn't run yet). Clear it —
        // this cap was factory-created, not scene-placed.
        cap.MarkFactoryCreated();

        CapRegistry.Register(cap);
        return cap;
    }

    /// <summary>
    /// Creates a cap from a composed deck entry. Instantiates the base prefab
    /// (visual only), then dynamically adds ability components based on the
    /// entry's level sliders (0 = no ability, 1-3 = ability with that level).
    /// Ability parameters (radius, force, VFX, etc.) are copied from the deck's
    /// template prefabs.
    ///
    /// The cap's RunDeckEntryId is set to a new unique ID (from the internal
    /// counter). RunManager uses this ID to identify which deck entry a lost
    /// cap came from — no sprite matching needed.
    ///
    /// If replaceExisting is false and the base prefab already has an ability
    /// component (e.g., it's a captured clone from a gained enemy cap), the
    /// existing component is kept and its level is NOT overridden — the clone's
    /// state is preserved exactly.
    ///
    /// If replaceExisting is true (used by the scene-placed enemy cap replacement
    /// pass), ALL existing ability components are deleted first, then fresh ones
    /// are added from the deck entry. This is the "delete old + replace with deck"
    /// behavior: the scene-placed cap's baked-in abilities are removed, and the
    /// deck entry's abilities take their place.
    /// </summary>
    public static Cap CreateComposed(
        Cap basePrefab,
        CapDeckDefinition.ComposedCapEntry entry,
        CapDeckDefinition deck,
        Vector2 groundPosition,
        bool isFace,
        CapOwner owner,
        bool replaceExisting = false)
    {
        if (basePrefab == null)
        {
            Debug.LogError("[CapFactory] BasePrefab is null.");
            return null;
        }

        // Instantiate the base cap (visual only).
        Vector3 worldPos = CapMath.FromXZ(groundPosition, 0f);
        Cap instance = Object.Instantiate(basePrefab, worldPos, Quaternion.identity);

        Vector3 p = instance.transform.position;
        p.y = 0.05f;
        instance.transform.position = p;

        Cap cap = instance.GetComponent<Cap>();
        if (cap == null) cap = instance.gameObject.AddComponent<Cap>();

        cap.Configure(_nextStableId++, isFace, owner);
        cap.MarkFactoryCreated();

        // If replaceExisting, delete ALL existing ability components first.
        // This is used by the scene-placed enemy cap replacement pass: the
        // scene-placed cap's baked-in abilities are removed, and the deck
        // entry's abilities take their place.
        if (replaceExisting)
        {
            DestroyExistingAbilities(cap.gameObject);
        }

        // Add abilities based on the entry's level sliders. Skip if the cap
        // already has the component (can only happen when replaceExisting is
        // false and the cap is a captured clone).
        if (entry.BombLevel > 0 && cap.GetComponent<BombCapFlipEffect>() == null)
        {
            var bomb = cap.gameObject.AddComponent<BombCapFlipEffect>();
            if (deck != null && deck.BombTemplate != null)
            {
                var templateBomb = deck.BombTemplate.GetComponent<BombCapFlipEffect>();
                if (templateBomb != null) bomb.CopyFrom(templateBomb);
            }
            bomb.SetLevel(entry.BombLevel);
        }

        if (entry.FlipperLevel > 0 && cap.GetComponent<FlipperCapEffect>() == null)
        {
            var flipper = cap.gameObject.AddComponent<FlipperCapEffect>();
            if (deck != null && deck.FlipperTemplate != null)
            {
                var templateFlipper = deck.FlipperTemplate.GetComponent<FlipperCapEffect>();
                if (templateFlipper != null) flipper.CopyFrom(templateFlipper);
            }
            flipper.SetLevel(entry.FlipperLevel);
        }

        if (entry.DefenderLevel > 0 && cap.GetComponent<DefenderCapEffect>() == null)
        {
            var defender = cap.gameObject.AddComponent<DefenderCapEffect>();
            if (deck != null && deck.DefenderTemplate != null)
            {
                var templateDefender = deck.DefenderTemplate.GetComponent<DefenderCapEffect>();
                if (templateDefender != null) defender.CopyFrom(templateDefender);
            }
            defender.SetLevel(entry.DefenderLevel);
        }

        if (entry.PredictorLevel > 0 && cap.GetComponent<PredictorCapEffect>() == null)
        {
            var predictor = cap.gameObject.AddComponent<PredictorCapEffect>();
            if (deck != null && deck.PredictorTemplate != null)
            {
                var templatePredictor = deck.PredictorTemplate.GetComponent<PredictorCapEffect>();
                if (templatePredictor != null) predictor.CopyFrom(templatePredictor);
            }
            predictor.SetLevel(entry.PredictorLevel);
        }

        // IMPORTANT: re-cache the cap's _flipEffects array. Cap.Awake ran
        // during Instantiate (before the ability components were added), so
        // _flipEffects is empty. CapEffectResolver uses FlipEffects to iterate
        // effects — without this refresh, the dynamically-added abilities are
        // invisible to the resolver and never trigger (even though they show
        // stickers, because StickerManager reads GetComponents live each frame).
        cap.RefreshFlipEffects();

        // Stamp the cap with a unique run deck entry ID. RunManager uses this
        // to identify which deck entry a lost cap came from.
        cap.RunDeckEntryId = _nextRunDeckEntryId++;

        CapRegistry.Register(cap);
        return cap;
    }

    /// <summary>
    /// Destroys all ability components on the given GameObject. Used by
    /// CreateComposed when replaceExisting is true — the scene-placed cap's
    /// baked-in abilities are removed so the deck entry's abilities can take
    /// their place.
    /// </summary>
    static void DestroyExistingAbilities(GameObject go)
    {
        BombCapFlipEffect bomb = go.GetComponent<BombCapFlipEffect>();
        if (bomb != null) Object.Destroy(bomb);
        FlipperCapEffect flipper = go.GetComponent<FlipperCapEffect>();
        if (flipper != null) Object.Destroy(flipper);
        DefenderCapEffect defender = go.GetComponent<DefenderCapEffect>();
        if (defender != null) Object.Destroy(defender);
        PredictorCapEffect predictor = go.GetComponent<PredictorCapEffect>();
        if (predictor != null) Object.Destroy(predictor);
    }

    public static void ResetIdCounter(int startId = 1)
    {
        _nextStableId = startId;
    }

    /// <summary>
    /// Returns the next stable ID and increments the counter. Used by Cap.Awake
    /// to assign IDs to caps placed in the scene editor (which don't go through
    /// CapFactory.Create).
    /// </summary>
    public static int NextStableId()
    {
        return _nextStableId++;
    }

    /// <summary>
    /// Allocates a new run deck entry ID. Called by RunManager when adding a
    /// gained enemy cap to the run deck (the cap was captured as a clone, so
    /// its RunDeckEntryId was set at clone time — but the run deck entry
    /// itself needs an ID for loss tracking).
    /// </summary>
    public static int NextRunDeckEntryId()
    {
        return _nextRunDeckEntryId++;
    }
}
