using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Removes caps after their center leaves the field in the XZ plane.
/// A cap must enter the field at least once before it can be removed, so a waiting
/// cap may safely stay at a spawn point outside the field.
/// The cap leaves the game logic immediately and then falls off the table as a physics object.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(BoxCollider))]
public sealed class CapFieldBoundary : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private BoxCollider _fieldCollider;

    [Header("Fall off the field")]
    [Tooltip("Extra outward speed given to a cap the moment it leaves the field, so it tips over the edge.")]
    [Min(0f)][SerializeField] private float _fallPushSpeed = 1.5f;
    [Tooltip("Upper limit for the speed a cap keeps from its last movement when it starts to fall. " +
             "Low values keep the cap over the table long enough to land on its edge first.")]
    [Min(0f)][SerializeField] private float _fallMaxSpeed = 3f;
    [Tooltip("Tumble speed (radians/second) around the edge the cap falls over.")]
    [Min(0f)][SerializeField] private float _fallSpin = 6f;
    [Tooltip("Gravity multiplier for a falling cap. Caps are large, so scene gravity alone looks floaty.")]
    [Range(1f, 20f)][SerializeField] private float _fallGravityScale = 6f;
    [Tooltip("World height at which a falling cap disappears.")]
    [SerializeField] private float _vanishHeight = -3f;
    [Tooltip("Safety timeout: seconds after which a fallen cap is removed even if it never reached the vanish height.")]
    [Min(0.1f)][SerializeField] private float _fallLifetime = 6f;

    /// <summary>Raised when a falling cap lands on the field while tipping over the edge.</summary>
    public event System.Action<Vector3> OnFallingCapHitField;

    /// <summary>Raised when a falling cap drops below the vanish height and is removed.</summary>
    public event System.Action<Vector3> OnFallingCapVanished;

    // Last position of every cap that was inside the field. Being in this map means the cap has
    // entered the field at least once; the stored position gives the cap its speed when it falls.
    private readonly Dictionary<Cap, Vector2> _groundPositionsInField = new();
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

            if (ContainsGroundPoint(cap.GroundPosition))
            {
                _groundPositionsInField[cap] = cap.GroundPosition;
                continue;
            }

            if (!_groundPositionsInField.TryGetValue(cap, out Vector2 lastPositionInField)) continue;
            _groundPositionsInField.Remove(cap);

            // Unregister now rather than waiting for the fall animation to finish.
            // This prevents scoring and chain-reaction code from seeing a removed cap.
            CapRegistry.Unregister(cap);
            DropCap(cap, lastPositionInField);
        }
    }

    void DropCap(Cap cap, Vector2 lastPositionInField)
    {
        Vector3 colliderWorldCenter = _fieldCollider.transform.TransformPoint(_fieldCollider.center);
        Vector2 fieldCenter = CapMath.ToXZ(colliderWorldCenter);

        // Carry over the movement of the last frame so a cap that was flying keeps flying.
        Vector2 groundVelocity = Time.deltaTime > 0f
            ? (cap.GroundPosition - lastPositionInField) / Time.deltaTime
            : Vector2.zero;

        var settings = new FallingCap.Settings
        {
            FieldCenter = fieldCenter,
            GroundVelocity = Vector2.ClampMagnitude(groundVelocity, _fallMaxSpeed),
            PushSpeed = _fallPushSpeed,
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

    internal void ReportFallingCapHitField(Vector3 position) => OnFallingCapHitField?.Invoke(position);

    internal void ReportFallingCapVanished(Vector3 position) => OnFallingCapVanished?.Invoke(position);

    void ForgetDestroyedCaps()
    {
        _destroyedCaps.Clear();
        foreach (KeyValuePair<Cap, Vector2> entry in _groundPositionsInField)
        {
            if (entry.Key == null)
                _destroyedCaps.Add(entry.Key);
        }

        for (int i = 0; i < _destroyedCaps.Count; i++)
            _groundPositionsInField.Remove(_destroyedCaps[i]);
    }

    void OnDisable()
    {
        _groundPositionsInField.Clear();
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
