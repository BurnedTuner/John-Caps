using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The opponent's supply of caps, configured per scene.
///
/// The deck is a queue: only the cap on top can be thrown, and it is instantiated one at a time at
/// the spawn point, exactly the way CapThrower prepares the player's cap. The spawn point must sit
/// outside the field — a waiting cap is registered like any other, and CapFieldBoundary only leaves
/// it alone as long as it has never touched the field.
///
/// The deck uses a <see cref="CapDeckDefinition"/> asset — the SAME format as the player's deck.
/// Assign a CapDeckDefinition to <see cref="DeckTemplate"/> in the inspector.
/// </summary>
[DisallowMultipleComponent]
public sealed class OpponentCapPool : MonoBehaviour
{
    [Header("Deck")]
    [Tooltip("Cap deck template (same format as the player's deck). The pool " +
             "rebuilds from this asset on Awake and on every board reset.")]
    [SerializeField] private CapDeckDefinition _deckTemplate;

    [Tooltip("Shuffle the deck when the match starts. Overrides the deck template's ShuffleOnStart.")]
    [SerializeField] private bool _shuffle = true;

    [Tooltip("Fixed shuffle seed for reproducible runs. 0 uses the global random state.")]
    [SerializeField] private int _randomSeed;

    [Header("Spawn")]
    [Tooltip("Where the waiting cap sits. Must be outside the field, like the player's spawn point.")]
    [SerializeField] private Transform _spawnPoint;

    [Tooltip("Owner stamped onto every cap this pool creates.")]
    [SerializeField] private CapOwner _owner = CapOwner.Opponent;

    private readonly List<Cap> _deck = new();
    private int _nextIndex;

    /// <summary>Caps still left in the deck, the one already waiting on the table excluded.</summary>
    public int Remaining => Mathf.Max(0, _deck.Count - _nextIndex);
    public bool IsEmpty => Remaining <= 0;
    public CapOwner Owner => _owner;

    public Vector3 SpawnPosition => _spawnPoint != null ? _spawnPoint.position : transform.position;

    void Awake()
    {
        Rebuild();
    }

    /// <summary>Refills the deck from the deck template. Called on Awake and on every board reset.</summary>
    public void Rebuild()
    {
        _deck.Clear();
        _nextIndex = 0;

        if (_deckTemplate == null || _deckTemplate.Caps == null)
        {
            Debug.LogWarning($"[OpponentCapPool] No DeckTemplate assigned on {gameObject.name}. " +
                             "The AI has no caps to throw.", this);
            return;
        }

        for (int i = 0; i < _deckTemplate.Caps.Length; i++)
        {
            if (_deckTemplate.Caps[i] != null)
                _deck.Add(_deckTemplate.Caps[i]);
        }

        if (_shuffle)
            Shuffle();
    }

    /// <summary>The prefab of the cap currently on top of the deck, or null when it is empty.</summary>
    public Cap PeekNextPrefab() => IsEmpty ? null : _deck[_nextIndex];

    /// <summary>
    /// Instantiates the cap on top of the deck at the spawn point. The deck is not advanced yet —
    /// call <see cref="Consume"/> once the throw has actually been accepted.
    /// </summary>
    public Cap SpawnNext()
    {
        Cap prefab = PeekNextPrefab();
        if (prefab == null) return null;

        Vector3 spawnPosition = SpawnPosition;
        Cap cap = CapFactory.Create(prefab, CapMath.ToXZ(spawnPosition), isHeads: true, _owner);
        if (cap != null)
        {
            cap.transform.position = spawnPosition;

            // Waiting, not in play: the resolver must leave its transform alone and chains must miss it.
            cap.SetParked(true);
        }

        return cap;
    }

    /// <summary>Drops the top cap from the deck after it has been thrown.</summary>
    public void Consume()
    {
        if (!IsEmpty) _nextIndex++;
    }

    void Shuffle()
    {
        Random.State previousState = default;
        bool useFixedSeed = _randomSeed != 0;

        if (useFixedSeed)
        {
            previousState = Random.state;
            Random.InitState(_randomSeed);
        }

        for (int i = _deck.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (_deck[i], _deck[j]) = (_deck[j], _deck[i]);
        }

        if (useFixedSeed)
            Random.state = previousState;
    }
}
