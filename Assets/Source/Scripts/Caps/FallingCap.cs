using UnityEngine;

/// <summary>
/// Turns a cap that left the field into a plain physics object so it visibly tips over the edge
/// and falls down. By the time this runs the cap is already out of the game logic, so the fall is
/// purely cosmetic: it never collides with caps that are still in play.
/// The cap reports the moment it drops below the vanish height, where it disappears.
/// </summary>
[DisallowMultipleComponent]
public sealed class FallingCap : MonoBehaviour
{
    public struct Settings
    {
        public Vector2 FieldCenter;
        public Vector3 Velocity;
        public float Spin;
        public float GravityScale;
        public float VanishHeight;
        public float MaximumLifetime;
    }

    private CapFieldBoundary _owner;
    private Settings _settings;
    private Rigidbody _body;
    private float _remainingLifetime;

    /// <summary>Hands the cap over to the physics engine.</summary>
    public static void Begin(Cap cap, CapFieldBoundary owner, in Settings settings)
    {
        if (cap == null || cap.GetComponent<FallingCap>() != null) return;

        GameObject capObject = cap.gameObject;
        cap.LeaveGame();
        cap.enabled = false;

        Vector2 outward = CapMath.ToXZ(capObject.transform.position) - settings.FieldCenter;
        Vector3 outwardDirection = outward.sqrMagnitude > 0.000001f
            ? new Vector3(outward.x, 0f, outward.y).normalized
            : Vector3.forward;

        Rigidbody body = capObject.GetComponent<Rigidbody>();
        if (body == null) body = capObject.AddComponent<Rigidbody>();
        body.isKinematic = false;
        // Gravity is applied by hand in FixedUpdate so caps can fall heavier than the scene gravity.
        body.useGravity = false;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        body.linearVelocity = settings.Velocity;
        // Tips the outer edge downwards, so the cap rolls over the border instead of sliding off flat.
        body.angularVelocity = Vector3.Cross(Vector3.up, outwardDirection) * settings.Spin;

        FallingCap falling = capObject.AddComponent<FallingCap>();
        falling._owner = owner;
        falling._settings = settings;
        falling._body = body;
        falling._remainingLifetime = settings.MaximumLifetime;
        falling.IgnoreCollisionsWithOtherCaps();
    }

    void IgnoreCollisionsWithOtherCaps()
    {
        Collider ownCollider = GetComponent<Collider>();
        if (ownCollider == null) return;

        // Caps on the table are kinematic and often overlap each other, so colliding with them would
        // fling the falling cap around instead of letting it drop off the edge. After this the cap
        // can only touch the field itself.
        // FindObjectsInactive.Include also covers caps whose component was switched off while falling.
        Cap[] caps = FindObjectsByType<Cap>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < caps.Length; i++)
        {
            if (caps[i].gameObject == gameObject) continue;

            Collider otherCollider = caps[i].GetComponent<Collider>();
            if (otherCollider == null || !otherCollider.enabled) continue;
            if (!otherCollider.gameObject.activeInHierarchy) continue;

            Physics.IgnoreCollision(ownCollider, otherCollider);
        }
    }

    void FixedUpdate()
    {
        if (_body == null) return;

        // ForceMode.Acceleration ignores mass, so the scale reads as a plain gravity multiplier.
        _body.AddForce(Physics.gravity * _settings.GravityScale, ForceMode.Acceleration);
    }

    void Update()
    {
        if (transform.position.y <= _settings.VanishHeight)
        {
            if (_owner != null) _owner.ReportFallingCapVanished(transform.position);
            Destroy(gameObject);
            return;
        }

        // Safety net for a cap that never reaches the vanish height.
        _remainingLifetime -= Time.deltaTime;
        if (_remainingLifetime <= 0f)
            Destroy(gameObject);
    }
}
