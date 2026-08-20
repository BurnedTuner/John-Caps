using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Owns the player's cap deck + hand state, and the 3D visual representation
/// of caps around the spawn point.
///
/// DECK: a runtime <see cref="List{Cap}"/> of cap PREFABS (not instantiated).
/// Mutated during play — caps are added on win, removed on lose, drawn into hand.
/// Restored from <see cref="DeckTemplate"/> on <see cref="ResetHand"/>.
///
/// HAND: a fixed-size list of <see cref="HandSize"/> slots, each holding an
/// instantiated Cap GameObject (or null if the slot is empty). All caps sit
/// in slots arranged around the spawn point — there is NO "active" cap.
/// The player can click and drag ANY cap in the hand to throw it.
///
/// Layout (3-cap hand, no active slot — all caps in a row behind spawn):
///   [Slot0] [Slot1] [Slot2]
///   All offset behind the spawn point, evenly spaced.
/// </summary>
public class CapHand : MonoBehaviour
{
    [Header("Deck")]
    [Tooltip("The read-only deck template asset. The live deck is restored from this on reset. " +
             "Create one via Create → Game → Cap Deck, then assign here.")]
    public CapDeckDefinition DeckTemplate;

    [Header("Layout")]
    [Tooltip("Camera that renders the hand caps. Used for screen-space cursor detection.")]
    public Camera HandCamera;

    [Tooltip("Transform that anchors the hand position. Caps are positioned relative to " +
             "this transform, so moving it moves the hand. Usually a child of the HandCamera " +
             "(e.g. the SpawnPoint). If null, uses the HandCamera's transform.")]
    public Transform HandAnchor;

    [Tooltip("How many caps the hand can hold simultaneously.")]
    [Min(1)] public int HandSize = 3;

    [Tooltip("Lateral distance between adjacent hand caps (in world units).")]
    [Min(0f)] public float SlotSpacing = 1.2f;

    [Tooltip("Distance from the HandAnchor at which caps are placed (along the anchor's forward).")]
    [Min(0f)] public float HandDepth = 0f;

    [Tooltip("Vertical offset of the hand row relative to the HandAnchor (along the anchor's up).")]
    public float HandVerticalOffset = 0f;

    [Tooltip("Screen-space radius (in pixels) around a hand cap where the player must " +
             "press to start a drag-throw. Uses the HandCamera for screen projection.")]
    [Min(10f)] public float CapGrabRadiusPixels = 80f;

    [Header("Ownership")]
    public CapOwner Owner = CapOwner.Player;

    [Header("Layers")]
    [Tooltip("Layer for caps while in hand (renders above the field).")]
    public int PlayerHandLayer = 0;

    // -----------------------------------------------------------------------
    // Live state
    // -----------------------------------------------------------------------

    /// <summary>Mutable live deck — cap prefabs not yet drawn into the hand.</summary>
    private readonly List<Cap> _deckPrefabs = new();

    /// <summary>Hand slots — instantiated Cap GameObjects. Null = empty slot.</summary>
    private readonly List<Cap> _handCaps = new();

    // -----------------------------------------------------------------------
    // Public API — queries
    // -----------------------------------------------------------------------

    /// <summary>Number of cap prefabs remaining in the deck (not yet drawn).</summary>
    public int DeckCount => _deckPrefabs.Count;

    /// <summary>Number of non-empty hand slots.</summary>
    public int HandCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _handCaps.Count; i++)
                if (_handCaps[i] != null) count++;
            return count;
        }
    }

    /// <summary>True if any cap is in the hand and can be thrown.</summary>
    public bool HasCapToThrow()
    {
        for (int i = 0; i < _handCaps.Count; i++)
            if (_handCaps[i] != null) return true;
        return false;
    }

    /// <summary>Get the cap at hand slot index, or null if empty.</summary>
    public Cap GetHandCap(int index)
    {
        if (index < 0 || index >= _handCaps.Count) return null;
        return _handCaps[index];
    }

    /// <summary>
    /// Returns the world position of the hand slot where the given cap would
    /// be if it weren't held/throwing/flying. Used by CapThrower to keep the
    /// held cap following its hand slot when the HandAnchor (and therefore
    /// the HandCamera) moves during aiming — without this, the held cap is
    /// pinned to its click-time world position, so when the camera moves the
    /// cap (and the trajectory origin computed from it) doesn't follow.
    /// </summary>
    public Vector3 GetCapSlotPosition(Cap cap)
    {
        if (cap == null) return Vector3.zero;

        Transform anchor = HandAnchor != null ? HandAnchor
            : (HandCamera != null ? HandCamera.transform : transform);
        if (anchor == null) return Vector3.zero;

        Vector3 anchorPos = anchor.position;
        Vector3 forward = anchor.forward;
        Vector3 up = anchor.up;
        Vector3 right = anchor.right;

        Vector3 rowCenter = anchorPos + forward * HandDepth + up * HandVerticalOffset;

        // Count non-null caps and find the given cap's placedIndex (same
        // logic as LayoutHand — caps are placed left-to-right, skipping nulls).
        int nonNullCount = 0;
        int capPlacedIndex = -1;
        for (int i = 0; i < _handCaps.Count; i++)
        {
            if (_handCaps[i] == null) continue;
            if (_handCaps[i] == cap) capPlacedIndex = nonNullCount;
            nonNullCount++;
        }

        if (capPlacedIndex < 0 || nonNullCount == 0) return anchorPos;

        float centerOffset = (nonNullCount - 1) * 0.5f;
        float slotOffset = capPlacedIndex - centerOffset;
        return rowCenter + right * (slotOffset * SlotSpacing);
    }

    /// <summary>
    /// Find the hand cap under the given screen position (in pixels).
    /// Returns the cap, or null if no cap is within CapGrabRadiusPixels.
    /// Uses the provided camera to project cap world positions to screen.
    /// </summary>
    public Cap GetCapUnderScreenPosition(Vector2 screenPos, Camera camera)
    {
        if (camera == null) return null;

        Cap best = null;
        float bestDist = float.PositiveInfinity;
        float grabRadius = CapGrabRadiusPixels;

        for (int i = 0; i < _handCaps.Count; i++)
        {
            Cap cap = _handCaps[i];
            if (cap == null) continue;
            // Skip caps that are being held/aimed/thrown.
            Cap.CapState state = cap.CurrentState;
            if (state == Cap.CapState.Held || state == Cap.CapState.Throwing || state == Cap.CapState.Flying)
                continue;

            Vector3 capScreenPos = camera.WorldToScreenPoint(cap.transform.position);
            if (capScreenPos.z < 0f) continue; // behind camera

            float dist = Vector2.Distance(screenPos, new Vector2(capScreenPos.x, capScreenPos.y));
            if (dist <= grabRadius && dist < bestDist)
            {
                bestDist = dist;
                best = cap;
            }
        }
        return best;
    }

    // -----------------------------------------------------------------------
    // Public API — mutations
    // -----------------------------------------------------------------------

    /// <summary>
    /// Draw a cap from the deck into the first empty hand slot. If the deck is
    /// empty, the slot stays empty.
    /// </summary>
    public void DrawFromDeck()
    {
        int emptySlot = _handCaps.IndexOf(null);
        if (emptySlot < 0) return; // hand is full
        if (_deckPrefabs.Count == 0) return; // deck is empty

        Cap prefab = _deckPrefabs[0];
        _deckPrefabs.RemoveAt(0);

        Cap instance = InstantiateCap(prefab);
        _handCaps[emptySlot] = instance;

        LayoutHand();
    }

    /// <summary>
    /// Refill all empty hand slots from the deck. Stops when hand is full or
    /// deck is empty.
    /// </summary>
    public void RefillHandFromDeck()
    {
        bool changed = false;
        for (int i = 0; i < _handCaps.Count; i++)
        {
            if (_handCaps[i] == null && _deckPrefabs.Count > 0)
            {
                Cap prefab = _deckPrefabs[0];
                _deckPrefabs.RemoveAt(0);
                _handCaps[i] = InstantiateCap(prefab);
                changed = true;
            }
        }
        if (changed) LayoutHand();
    }

    /// <summary>
    /// Full reset: destroy all hand caps, restore the deck from the template
    /// (optionally shuffled), refill the hand. Call on board reset / new round.
    /// </summary>
    public void ResetHand()
    {
        // Destroy all hand cap GameObjects.
        for (int i = 0; i < _handCaps.Count; i++)
        {
            if (_handCaps[i] != null)
            {
                // Unregister so CapRegistry doesn't hold a stale reference.
                CapRegistry.Unregister(_handCaps[i]);
                Destroy(_handCaps[i].gameObject);
                _handCaps[i] = null;
            }
        }
        _handCaps.Clear();
        _deckPrefabs.Clear();

        // Restore deck from template.
        if (DeckTemplate != null && DeckTemplate.Caps != null)
        {
            for (int i = 0; i < DeckTemplate.Caps.Length; i++)
            {
                if (DeckTemplate.Caps[i] != null)
                    _deckPrefabs.Add(DeckTemplate.Caps[i]);
            }

            if (DeckTemplate.ShuffleOnStart)
                ShuffleDeck();
        }

        // Initialize hand slots to null up to HandSize.
        for (int i = 0; i < HandSize; i++)
            _handCaps.Add(null);

        // Fill hand from deck.
        RefillHandFromDeck();

        LayoutHand();
    }

    /// <summary>
    /// Remove the given cap from the hand (it's been thrown). The slot becomes
    /// null. Does NOT draw a new cap — call <see cref="DrawFromDeck"/> afterward.
    /// </summary>
    public void ClearSlot(Cap cap)
    {
        if (cap == null) return;
        for (int i = 0; i < _handCaps.Count; i++)
        {
            if (_handCaps[i] == cap)
            {
                _handCaps[i] = null;
                return;
            }
        }
    }

    /// <summary>
    /// Return a cap to the hand — find the first empty slot and place it there.
    /// Used when a drag-throw is cancelled. Does NOT change the cap's state
    /// (caller is responsible for EndHeldToIdle).
    /// </summary>
    public void ReturnCapToHand(Cap cap)
    {
        if (cap == null) return;
        int emptySlot = _handCaps.IndexOf(null);
        if (emptySlot >= 0)
            _handCaps[emptySlot] = cap;
        LayoutHand();
    }

    // -----------------------------------------------------------------------
    // Public API — win/lose mutations
    // -----------------------------------------------------------------------

    /// <summary>
    /// Add a cap prefab to the deck (e.g. player won a fight). The cap will be
    /// drawn into the hand on the next <see cref="DrawFromDeck"/> call.
    /// </summary>
    public void AddCap(Cap prefab)
    {
        if (prefab == null) return;
        _deckPrefabs.Add(prefab);
    }

    /// <summary>
    /// Remove the first matching cap prefab from the deck (e.g. player lost a
    /// fight). No-op if the prefab isn't in the deck.
    /// </summary>
    public void RemoveCap(Cap prefab)
    {
        if (prefab == null) return;
        _deckPrefabs.Remove(prefab);
    }

    /// <summary>
    /// Remove a random cap prefab from the deck. No-op if the deck is empty.
    /// </summary>
    public void RemoveRandomCap()
    {
        if (_deckPrefabs.Count == 0) return;
        int idx = Random.Range(0, _deckPrefabs.Count);
        _deckPrefabs.RemoveAt(idx);
    }

    /// <summary>Shuffle the deck order. Useful after AddCap calls.</summary>
    public void ShuffleDeck()
    {
        for (int i = _deckPrefabs.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (_deckPrefabs[i], _deckPrefabs[j]) = (_deckPrefabs[j], _deckPrefabs[i]);
        }
    }

    // -----------------------------------------------------------------------
    // Internals
    // -----------------------------------------------------------------------

    void Awake()
    {
        // Initialize hand slots to null up to HandSize so layout works
        // even before ResetHand is called.
        if (_handCaps.Count == 0)
        {
            for (int i = 0; i < HandSize; i++)
                _handCaps.Add(null);
        }
    }

    void Start()
    {
        // If a deck template is assigned, do the initial reset automatically.
        if (DeckTemplate != null)
            ResetHand();
    }

    /// <summary>
    /// Re-layout the hand every frame so caps follow the spawn point when the
    /// camera moves. Cheap (just sets transform.position on a handful of caps).
    /// Skips caps that are currently being held/aimed (CapThrower manages their
    /// position during aiming).
    /// </summary>
    void Update()
    {
        // Only re-layout if we have at least one cap in hand.
        bool anyCap = false;
        for (int i = 0; i < _handCaps.Count; i++)
        {
            if (_handCaps[i] != null) { anyCap = true; break; }
        }
        if (!anyCap) return;

        LayoutHand();
    }

    Cap InstantiateCap(Cap prefab)
    {
        if (prefab == null) return null;

        Transform anchor = HandAnchor != null ? HandAnchor
            : (HandCamera != null ? HandCamera.transform : transform);
        Vector3 spawnPos = anchor != null ? anchor.position : Vector3.zero;
        Cap instance = CapFactory.Create(
            prefab,
            CapMath.ToXZ(spawnPos),
            isHeads: true,
            Owner);

        if (instance != null)
        {
            instance.transform.position = spawnPos;
            SetCapLayerRecursive(instance.gameObject, PlayerHandLayer);

            // Make the cap non-interactable while it's in the hand:
            //   - SetImmutable prevents BeginLaunch (chain hits) and BeginPush (push radius).
            //   - Unregister from CapRegistry so it doesn't appear in AllCaps — the
            //     simulation, predictor, and CollectDirectHitPredictions all iterate
            //     CapRegistry.AllCaps, so an unregistered cap is invisible to them.
            //   - Destroy any Collider so physics overlaps (OverlapSphereNonAlloc in
            //     OverlapsAimBlockingZone, RaycastNonAlloc in TryGetFieldPoint) ignore it.
            // The cap is re-registered and made mutable when thrown (see CapThrower.Fire).
            instance.SetImmutable(true);
            CapRegistry.Unregister(instance);
            DestroyCollidersRecursive(instance.gameObject);
        }
        return instance;
    }

    /// <summary>
    /// Called by CapThrower.Fire() right before submitting the throw request.
    /// Makes the cap interactable again: re-registers it in CapRegistry and
    /// clears the immutable flag so the simulation can flip/push it.
    /// </summary>
    public void ReleaseCapForThrow(Cap cap)
    {
        if (cap == null) return;
        cap.SetImmutable(false);
        // Do NOT clear the hand flip — the cap should keep its hand rotation
        // through the throw, flight, and landing.
        if (!CapRegistry.Contains(cap))
            CapRegistry.Register(cap);

        // Re-create a collider if one was destroyed by InstantiateCap.
        // Hand caps have their colliders destroyed to prevent physics overlaps
        // while in hand. When thrown, the cap needs a collider again so that
        // raycasts (for sticker hover, aim detection, etc.) can hit it.
        if (cap.GetComponent<Collider>() == null)
        {
            float radius = cap.Parameters != null ? cap.Parameters.Radius : 0.5f;
            var collider = cap.gameObject.AddComponent<SphereCollider>();
            collider.radius = radius;
            collider.isTrigger = false;
        }
    }

    static void DestroyCollidersRecursive(GameObject obj)
    {
        Collider c = obj.GetComponent<Collider>();
        if (c != null) Destroy(c);
        foreach (Transform child in obj.transform)
        {
            if (child != null)
                DestroyCollidersRecursive(child.gameObject);
        }
    }

    /// <summary>
    /// Position all hand caps in a row at the HandAnchor. Caps are placed
    /// relative to the anchor's transform (position + rotation), so moving the
    /// anchor moves the hand. Caps face the anchor's forward direction.
    /// Caps currently in Held/Throwing/Flying state are skipped — CapThrower
    /// and the simulation own their position during aiming/throw.
    /// </summary>
    void LayoutHand()
    {
        Transform anchor = HandAnchor != null ? HandAnchor
            : (HandCamera != null ? HandCamera.transform : transform);
        if (anchor == null) return;

        Vector3 anchorPos = anchor.position;
        Vector3 forward = anchor.forward;
        Vector3 right = anchor.right;
        Vector3 up = anchor.up;

        // Center of the hand row: offset from the anchor by HandDepth and HandVerticalOffset.
        Vector3 rowCenter = anchorPos + forward * HandDepth + up * HandVerticalOffset;

        // Count non-null caps to center them.
        int nonNullCount = 0;
        for (int i = 0; i < _handCaps.Count; i++)
            if (_handCaps[i] != null) nonNullCount++;

        if (nonNullCount == 0) return;

        int placedIndex = 0;
        for (int i = 0; i < _handCaps.Count; i++)
        {
            Cap cap = _handCaps[i];
            if (cap == null) continue;

            // Skip caps that are being held/aimed/thrown.
            Cap.CapState state = cap.CurrentState;
            if (state == Cap.CapState.Held || state == Cap.CapState.Throwing || state == Cap.CapState.Flying)
            {
                placedIndex++;
                continue;
            }

            // Center the row of caps.
            float centerOffset = (nonNullCount - 1) * 0.5f;
            float slotOffset = placedIndex - centerOffset;

            Vector3 slotPos = rowCenter + right * (slotOffset * SlotSpacing);
            cap.transform.position = slotPos;
            // Caps face the anchor's forward (toward the camera), with the
            // correct side up based on IsHeads, plus the hand-flip Y rotation.
            Quaternion faceCam = Quaternion.LookRotation(-forward, up);
            Quaternion sideRot = cap.IsHeads ? Quaternion.identity : Quaternion.Euler(180f, 0f, 0f);
            Quaternion flipRot = Quaternion.Euler(0f, cap.HandFlipYaw, 0f);
            cap.transform.rotation = faceCam * flipRot * sideRot;

            placedIndex++;
        }
    }

    /// <summary>
    /// Re-apply the hand layer to all hand caps. Call after any layer change.
    /// </summary>
    public void RefreshLayers()
    {
        for (int i = 0; i < _handCaps.Count; i++)
        {
            if (_handCaps[i] != null)
                SetCapLayerRecursive(_handCaps[i].gameObject, PlayerHandLayer);
        }
    }

    static void SetCapLayerRecursive(GameObject obj, int layer)
    {
        obj.layer = layer;
        foreach (Transform child in obj.transform)
        {
            if (child != null)
                SetCapLayerRecursive(child.gameObject, layer);
        }
    }
}
