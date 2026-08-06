using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Removes caps after their center leaves the field in the XZ plane.
/// A cap must enter the field at least once before it can be removed, so a waiting
/// cap may safely stay at a spawn point outside the field.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(BoxCollider))]
public sealed class CapFieldBoundary : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private BoxCollider _fieldCollider;

    private readonly HashSet<Cap> _capsThatEnteredField = new();

    void Reset()
    {
        ResolveCollider();
    }

    void Awake()
    {
        ResolveCollider();
    }

    void OnValidate()
    {
        ResolveCollider();
    }

    void LateUpdate()
    {
        if (_fieldCollider == null || !_fieldCollider.enabled) return;

        _capsThatEnteredField.RemoveWhere(cap => cap == null);

        // Iterate backwards because out-of-bounds caps are removed from the registry immediately.
        for (int i = CapRegistry.AllCaps.Count - 1; i >= 0; i--)
        {
            Cap cap = CapRegistry.AllCaps[i];
            if (cap == null)
            {
                CapRegistry.AllCaps.RemoveAt(i);
                continue;
            }

            if (ContainsGroundPoint(cap.GroundPosition))
            {
                _capsThatEnteredField.Add(cap);
                continue;
            }

            if (!_capsThatEnteredField.Remove(cap)) continue;

            // Unregister now rather than waiting for Destroy/OnDestroy at the end of the frame.
            // This prevents scoring and chain-reaction code from seeing a removed cap.
            CapRegistry.Unregister(cap);
            Destroy(cap.gameObject);
        }
    }

    void OnDisable()
    {
        _capsThatEnteredField.Clear();
    }

    bool ContainsGroundPoint(Vector2 groundPoint)
    {
        Vector3 colliderWorldCenter = _fieldCollider.transform.TransformPoint(_fieldCollider.center);
        Vector3 worldPoint = new Vector3(groundPoint.x, colliderWorldCenter.y, groundPoint.y);
        Vector3 localPoint = _fieldCollider.transform.InverseTransformPoint(worldPoint);
        Vector3 halfSize = _fieldCollider.size * 0.5f;
        Vector3 offset = localPoint - _fieldCollider.center;

        return Mathf.Abs(offset.x) <= halfSize.x
            && Mathf.Abs(offset.z) <= halfSize.z;
    }

    void ResolveCollider()
    {
        if (_fieldCollider == null)
            _fieldCollider = GetComponent<BoxCollider>();
    }
}
