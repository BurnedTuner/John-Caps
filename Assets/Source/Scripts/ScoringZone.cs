using System.Collections.Generic;
using UnityEngine;

public readonly struct CapCounts
{
    public int Player { get; }
    public int Opponent { get; }

    public CapCounts(int player, int opponent)
    {
        Player = player;
        Opponent = opponent;
    }
}

/// <summary>
/// Marks a scoring volume and optionally prevents the player from aiming a direct throw into it.
/// Caps moved by impacts and chain reactions are not restricted by this component.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(BoxCollider), typeof(Rigidbody))]
public sealed class ScoringZone : MonoBehaviour
{
    [Header("Aiming")]
    [SerializeField] private bool _blocksDirectAiming = true;

    public bool BlocksDirectAiming => _blocksDirectAiming;

    private const int OverlapBufferSize = 64;
    private readonly Collider[] _overlapBuffer = new Collider[OverlapBufferSize];
    private readonly HashSet<Cap> _capsInside = new();

    void Reset()
    {
        EnsurePhysicsConfiguration();
    }

    void OnValidate()
    {
        EnsurePhysicsConfiguration();
    }

    void EnsurePhysicsConfiguration()
    {
        if (TryGetComponent(out BoxCollider zoneCollider))
            zoneCollider.isTrigger = true;

        if (TryGetComponent(out Rigidbody zoneBody))
        {
            zoneBody.isKinematic = true;
            zoneBody.useGravity = false;
        }
    }

    public CapCounts GetCapCounts()
    {
        RefreshOccupants();

        int playerCaps = 0;
        int opponentCaps = 0;

        foreach (Cap cap in _capsInside)
        {
            if (cap == null) continue;

            switch (cap.Owner)
            {
                case CapOwner.Player:
                    playerCaps++;
                    break;
                case CapOwner.Opponent:
                    opponentCaps++;
                    break;
            }
        }

        return new CapCounts(playerCaps, opponentCaps);
    }

    void RefreshOccupants()
    {
        _capsInside.Clear();
        if (!TryGetComponent(out BoxCollider zoneCollider)) return;

        Physics.SyncTransforms();

        Vector3 worldCenter = transform.TransformPoint(zoneCollider.center);
        Vector3 scale = transform.lossyScale;
        Vector3 worldHalfExtents = Vector3.Scale(
            zoneCollider.size * 0.5f,
            new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));

        int hitCount = Physics.OverlapBoxNonAlloc(
            worldCenter,
            worldHalfExtents,
            _overlapBuffer,
            transform.rotation,
            ~0,
            QueryTriggerInteraction.Collide);

        for (int i = 0; i < hitCount; i++)
            TrackCap(_overlapBuffer[i]);

        if (hitCount == _overlapBuffer.Length)
        {
            Collider[] allHits = Physics.OverlapBox(
                worldCenter,
                worldHalfExtents,
                transform.rotation,
                ~0,
                QueryTriggerInteraction.Collide);

            for (int i = 0; i < allHits.Length; i++)
                TrackCap(allHits[i]);
        }
    }

    void TrackCap(Collider other)
    {
        if (other == null) return;

        Cap cap = other.GetComponentInParent<Cap>();
        if (cap == null) return;
        if (!CapRegistry.Contains(cap)) return;

        _capsInside.Add(cap);
    }

    void OnTriggerEnter(Collider other)
    {
        TrackCap(other);
    }

    void OnTriggerStay(Collider other)
    {
        TrackCap(other);
    }

    void OnTriggerExit(Collider other)
    {
        if (other == null) return;

        Cap cap = other.GetComponentInParent<Cap>();
        if (cap != null) _capsInside.Remove(cap);
    }

    void OnDisable()
    {
        _capsInside.Clear();
    }
}
