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
    [Tooltip("Tumble speed (radians/second) kept up while a cap is falling, after it has cleared the edge.")]
    [Min(0f)][SerializeField] private float _fallSpin = 1f;
    [Tooltip("How far a cap turns over the rim, in degrees, before it lets go and falls free. " +
             "Applies to a cap the table still holds up: its centre crossed the edge, but almost its " +
             "whole face is still over the table, so it has to topple before it can fall. Past 90° it " +
             "is unambiguously going over.")]
    [Range(45f, 180f)][SerializeField] private float _edgeToppleAngle = 105f;
    [Tooltip("Seconds the topple over the rim takes. Caps are wide (radius 1) and fall under scaled " +
             "gravity, so a real topple is quick — around 0.3 s.")]
    [Min(0.01f)][SerializeField] private float _edgeToppleDuration = 0.35f;
    [Tooltip("Gravity multiplier for a falling cap. Caps are large, so scene gravity alone looks floaty.")]
    [Range(1f, 20f)][SerializeField] private float _fallGravityScale = 6f;
    [Tooltip("World height at which a falling cap disappears.")]
    [SerializeField] private float _vanishHeight = -10f;
    [Tooltip("Safety timeout: seconds after which a fallen cap is removed even if it never reached the vanish height.")]
    [Min(0.1f)][SerializeField] private float _fallLifetime = 10f;

    /// <summary>Raised when a falling cap drops below the vanish height and is removed.</summary>
    public event System.Action<Vector3> OnFallingCapVanished;

    /// <summary>
    /// Raised for every cap the moment it leaves the field, before it is handed over to physics.
    /// This is the authoritative "a cap is out of the game" signal: counting registry membership is
    /// not equivalent, because a stacked cap also leaves the registry while staying on the table.
    /// A falling stack raises the event once per cap it consisted of.
    /// </summary>
    public event System.Action<Cap> OnCapLeftField;

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

    /// <summary>
    /// Distance in world units from a ground point to the nearest field edge.
    /// Positive inside the field, zero outside it. A cap standing this far from the edge needs at
    /// least this much travel to be knocked off, which is what makes the value useful as a measure
    /// of how exposed the cap is.
    /// </summary>
    public float DistanceToEdge(Vector2 groundPoint) => DistanceToEdge(groundPoint, out _);

    /// <summary>World-space bounds of the field, used to sweep candidate landing points.</summary>
    public Bounds FieldWorldBounds => _fieldCollider != null ? _fieldCollider.bounds : new Bounds();

    /// <summary>
    /// As <see cref="DistanceToEdge(Vector2)"/>, and also reports the point on the edge that is
    /// nearest. The direction towards it is the shortest way to shove a cap off the table.
    /// </summary>
    public float DistanceToEdge(Vector2 groundPoint, out Vector2 nearestEdgePoint)
    {
        nearestEdgePoint = groundPoint;
        if (_fieldCollider == null) return 0f;

        Transform fieldTransform = _fieldCollider.transform;
        Vector3 fieldCenter = fieldTransform.TransformPoint(_fieldCollider.center);
        Vector3 localPoint = fieldTransform.InverseTransformPoint(
            new Vector3(groundPoint.x, fieldCenter.y, groundPoint.y));

        Vector3 halfSize = _fieldCollider.size * 0.5f;
        Vector3 center = _fieldCollider.center;
        float minX = center.x - halfSize.x;
        float maxX = center.x + halfSize.x;
        float minZ = center.z - halfSize.z;
        float maxZ = center.z + halfSize.z;

        if (localPoint.x < minX || localPoint.x > maxX || localPoint.z < minZ || localPoint.z > maxZ)
        {
            float clampedX = Mathf.Clamp(localPoint.x, minX, maxX);
            float clampedZ = Mathf.Clamp(localPoint.z, minZ, maxZ);
            nearestEdgePoint = CapMath.ToXZ(fieldTransform.TransformPoint(
                new Vector3(clampedX, localPoint.y, clampedZ)));
            return 0f;
        }

        // Nearest point on the border: push the point out to whichever of the four sides is closest.
        // The distance is then measured in world space so field scale and rotation are respected.
        float toMinX = localPoint.x - minX;
        float toMaxX = maxX - localPoint.x;
        float toMinZ = localPoint.z - minZ;
        float toMaxZ = maxZ - localPoint.z;

        float nearest = toMinX;
        Vector3 borderPoint = new Vector3(minX, localPoint.y, localPoint.z);

        if (toMaxX < nearest)
        {
            nearest = toMaxX;
            borderPoint = new Vector3(maxX, localPoint.y, localPoint.z);
        }

        if (toMinZ < nearest)
        {
            nearest = toMinZ;
            borderPoint = new Vector3(localPoint.x, localPoint.y, minZ);
        }

        if (toMaxZ < nearest)
        {
            borderPoint = new Vector3(localPoint.x, localPoint.y, maxZ);
        }

        nearestEdgePoint = CapMath.ToXZ(fieldTransform.TransformPoint(borderPoint));
        return Vector2.Distance(groundPoint, nearestEdgePoint);
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

            // A parked cap waits at a thrower's spawn point rather than standing on the field, and its
            // GroundPosition is not even kept in sync with where it is drawn — so it must not be judged
            // by it. Said outright instead of relying on such a cap never having reached the field.
            if (cap.IsParked) continue;

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
                // Scene-placed caps are NOT unregistered — they need to stay in CapRegistry so
                // GameManager.ResetBoard can find and regenerate them. FallingCap.HandleVanish
                // hides them instead of destroying them.
                if (!cap.IsScenePlaced)
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
        DistanceToEdge(cap.GroundPosition, out Vector2 nearestEdgePoint);
        Vector3 outwardDirection = ResolveOutwardDirection(cap.GroundPosition, nearestEdgePoint);

        // A cap that still reaches the field has landed on it and has to topple over the rim before it
        // can fall — almost its whole face is still over the table. A cap that came down past the field
        // never touched anything, so it has nothing to topple over: it keeps the speed of its last move
        // and flies on.
        bool landedOnField = Supports(cap.GroundPosition, cap.Parameters.Radius);
        Vector3 velocity = !landedOnField && Time.deltaTime > 0f
            ? (cap.transform.position - previousPosition) / Time.deltaTime
            : Vector3.zero;

        var settings = new FallingCap.Settings
        {
            OutwardDirection = outwardDirection,
            TopplesOverEdge = landedOnField,
            Velocity = velocity,
            ToppleAngle = _edgeToppleAngle,
            ToppleDuration = _edgeToppleDuration,
            Spin = _fallSpin,
            GravityScale = _fallGravityScale,
            VanishHeight = _vanishHeight,
            MaximumLifetime = _fallLifetime
        };

        // A stack falls apart cap by cap, otherwise the caps on top would hang in the air.
        List<Cap> stack = cap.ReleaseStack();
        for (int i = 0; i < stack.Count; i++)
        {
            // Every cap topples around the edge under its own underside, so a stack does not swing about
            // the bottom cap's rim.
            settings.Pivot = ResolveTopplePivot(stack[i], nearestEdgePoint);

            // Announced before the fall starts, while the cap is still intact and readable.
            OnCapLeftField?.Invoke(stack[i]);
            FallingCap.Begin(stack[i], this, settings);
        }
    }

    /// <summary>
    /// The world point a cap topples around: on the field edge it crossed, at the height of the cap's
    /// underside. Read off the collider so a cap pivots on its own bottom face — pivoting on its centre
    /// would drive half of it down through the table as it turns.
    /// </summary>
    static Vector3 ResolveTopplePivot(Cap cap, Vector2 edgePoint)
    {
        Collider capCollider = cap.GetComponent<Collider>();
        float undersideY = capCollider != null && capCollider.enabled
            ? capCollider.bounds.min.y
            : cap.transform.position.y;

        return new Vector3(edgePoint.x, undersideY, edgePoint.y);
    }

    /// <summary>
    /// Which way to tip a cap that is leaving the field: the outward normal of the edge its centre
    /// crossed. <paramref name="nearestEdgePoint"/> comes from
    /// <see cref="DistanceToEdge(Vector2, out Vector2)"/>, which clamps a point outside the field onto
    /// the field box, so the offset between the two is that normal — and near a corner it is the corner
    /// diagonal, which is also the way the cap goes.
    ///
    /// That offset is only as long as the overhang, so for a cap sitting all but exactly on the edge it
    /// is too short to normalise reliably. Those fall back to pointing away from the field centre, which
    /// is the direction the cap has to travel anyway.
    /// </summary>
    Vector3 ResolveOutwardDirection(Vector2 groundPoint, Vector2 nearestEdgePoint)
    {
        Vector2 outward = groundPoint - nearestEdgePoint;

        // Kept above Vector3.normalized's own 1e-5 threshold, below which it returns a zero vector —
        // that would leave the cap with no topple axis at all.
        if (outward.sqrMagnitude <= 1e-8f)
            outward = groundPoint - FieldCenterXZ;

        return outward.sqrMagnitude > 1e-8f
            ? new Vector3(outward.x, 0f, outward.y).normalized
            : Vector3.forward;
    }

    /// <summary>The field's centre projected onto the ground plane.</summary>
    Vector2 FieldCenterXZ => _fieldCollider != null
        ? CapMath.ToXZ(_fieldCollider.transform.TransformPoint(_fieldCollider.center))
        : Vector2.zero;

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
