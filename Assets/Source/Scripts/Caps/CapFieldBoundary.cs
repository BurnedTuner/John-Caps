using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Removes caps after their center leaves the field in the XZ plane and hands them over to physics,
/// so they visibly fall off the table while the game logic treats them as gone.
/// Only settled caps are checked: a cap that is still animating finishes its move first, so it lands
/// exactly where the prediction promised and falls from there.
/// A cap must also reach the field at least once before it can be removed, so a waiting cap may
/// safely stay at a spawn point outside the field.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(BoxCollider))]
public sealed class CapFieldBoundary : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private BoxCollider _fieldCollider;

    [Header("Fall off the field")]
    [Tooltip("Tumble speed (radians/second) around the edge the cap falls over.")]
    [Min(0f)][SerializeField] private float _fallSpin = 1f;
    [Tooltip("Gravity multiplier for a falling cap. Caps are large, so scene gravity alone looks floaty.")]
    [Range(1f, 20f)][SerializeField] private float _fallGravityScale = 6f;
    [Tooltip("World height at which a falling cap disappears.")]
    [SerializeField] private float _vanishHeight = -10f;
    [Tooltip("Safety timeout: seconds after which a fallen cap is removed even if it never reached the vanish height.")]
    [Min(0.1f)][SerializeField] private float _fallLifetime = 10f;

    /// <summary>Raised when a falling cap drops below the vanish height and is removed.</summary>
    public event System.Action<Vector3> OnFallingCapVanished;

    struct CapTrail
    {
        public Vector3 PreviousPosition;
        public bool ReachedField;
    }

    private readonly Dictionary<Cap, CapTrail> _trails = new();
    private readonly List<Cap> _destroyedCaps = new();

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

    /// <summary>
    /// True when the field reaches a ground point that is <paramref name="radius"/> wide — that is,
    /// a cap of that radius standing there still rests on the field.
    /// A radius of zero asks whether the point itself is on the field.
    /// </summary>
    public bool Supports(Vector2 groundPoint, float radius)
    {
        if (_fieldCollider == null) return false;

        Transform fieldTransform = _fieldCollider.transform;
        Vector3 fieldCenter = fieldTransform.TransformPoint(_fieldCollider.center);
        Vector3 localPoint = fieldTransform.InverseTransformPoint(
            new Vector3(groundPoint.x, fieldCenter.y, groundPoint.y));

        Vector3 halfSize = _fieldCollider.size * 0.5f;
        Vector3 center = _fieldCollider.center;
        float nearestX = Mathf.Clamp(localPoint.x, center.x - halfSize.x, center.x + halfSize.x);
        float nearestZ = Mathf.Clamp(localPoint.z, center.z - halfSize.z, center.z + halfSize.z);

        if (nearestX == localPoint.x && nearestZ == localPoint.z) return true;

        Vector3 nearestFieldPoint = fieldTransform.TransformPoint(
            new Vector3(nearestX, localPoint.y, nearestZ));

        return Vector2.Distance(groundPoint, CapMath.ToXZ(nearestFieldPoint)) <= radius;
    }

    void LateUpdate()
    {
        if (_fieldCollider == null || !_fieldCollider.enabled) return;

        ForgetDestroyedCaps();

        // Iterate backwards because out-of-bounds caps are removed from the registry immediately.
        for (int i = CapRegistry.AllCaps.Count - 1; i >= 0; i--)
        {
            Cap cap = CapRegistry.AllCaps[i];
            if (cap == null)
            {
                CapRegistry.RemoveAt(i);
                continue;
            }

            _trails.TryGetValue(cap, out CapTrail trail);
            Vector3 currentPosition = cap.transform.position;

            if (Supports(cap.GroundPosition, 0f))
            {
                trail.ReachedField = true;
            }
            // A busy cap is still animating, so it is left alone until it has settled.
            else if (trail.ReachedField && !cap.IsBusy)
            {
                _trails.Remove(cap);

                // Unregister now rather than waiting for the fall to play out. This prevents scoring
                // and chain-reaction code from seeing a cap that is already out of the game.
                CapRegistry.Unregister(cap);
                DropCap(cap, trail.PreviousPosition);
                continue;
            }

            trail.PreviousPosition = currentPosition;
            _trails[cap] = trail;
        }
    }

    void DropCap(Cap cap, Vector3 previousPosition)
    {
        // A cap that still reaches the field has landed on it, so it starts falling from rest and
        // simply tips over the edge. A cap that came down past the field never touched anything, so
        // it keeps the speed of its last move and flies on.
        bool landedOnField = Supports(cap.GroundPosition, cap.Parameters.Radius);
        Vector3 velocity = landedOnField || Time.deltaTime <= 0f
            ? Vector3.zero
            : (cap.transform.position - previousPosition) / Time.deltaTime;

        var settings = new FallingCap.Settings
        {
            FieldCenter = CapMath.ToXZ(_fieldCollider.transform.TransformPoint(_fieldCollider.center)),
            Velocity = velocity,
            Spin = _fallSpin,
            GravityScale = _fallGravityScale,
            VanishHeight = _vanishHeight,
            MaximumLifetime = _fallLifetime
        };

        // A stack falls apart cap by cap, otherwise the caps on top would hang in the air.
        List<Cap> stack = cap.ReleaseStack();
        for (int i = 0; i < stack.Count; i++)
            FallingCap.Begin(stack[i], this, settings);
    }

    internal void ReportFallingCapVanished(Vector3 position) => OnFallingCapVanished?.Invoke(position);

    void ForgetDestroyedCaps()
    {
        _destroyedCaps.Clear();
        foreach (KeyValuePair<Cap, CapTrail> entry in _trails)
        {
            if (entry.Key == null)
                _destroyedCaps.Add(entry.Key);
        }

        for (int i = 0; i < _destroyedCaps.Count; i++)
            _trails.Remove(_destroyedCaps[i]);
    }

    void OnDisable()
    {
        _trails.Clear();
    }

    void ResolveCollider()
    {
        if (_fieldCollider == null)
            _fieldCollider = GetComponent<BoxCollider>();
    }
}
