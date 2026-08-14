using UnityEngine;

/// <summary>
/// Plays a cap that left the field falling off the table, in two beats: it topples over the edge its
/// centre crossed, then it falls free until it drops out of sight.
///
/// Hand-animated rather than handed to the physics engine, for the same reason every other cap motion
/// in the game is (see Cap.StepThrow / StepFly / StepPush). The fall is purely cosmetic — by the time
/// this runs the cap is already out of the game logic — and the case that matters most is the one a
/// rigid-body solver handles worst. A cap whose centre crossed the edge by a millimetre is still
/// resting on the table under almost its whole face, and PhysX reads that as a stable contact:
/// the moment that should tip it over has an arm only as long as the overhang, the default contact
/// offset (1 cm) is an order of magnitude wider than that arm, and the gravity scale that keeps caps
/// from falling floatily multiplies the restoring impulse by the same factor — as does the friction
/// holding the cap in place. Such a cap would tilt and settle back flat.
///
/// The cap reports the moment it drops below the vanish height, where it disappears.
/// </summary>
[DisallowMultipleComponent]
public sealed class FallingCap : MonoBehaviour
{
    public struct Settings
    {
        /// <summary>
        /// Outward normal of the field edge the cap's centre crossed — the way the cap goes over.
        /// Supplied by <see cref="CapFieldBoundary"/>, which knows that edge; measuring it from the
        /// field centre instead is off by up to 45° near a corner.
        /// </summary>
        public Vector3 OutwardDirection;

        /// <summary>
        /// World point the cap topples around: on the edge it crossed, at the height of its underside.
        /// Only read when <see cref="TopplesOverEdge"/> is set.
        /// </summary>
        public Vector3 Pivot;

        /// <summary>
        /// True for a cap the table still holds up, which has to topple over the rim before it can fall.
        /// False for one that came down past the field and never touched anything: it has nothing to
        /// topple over and simply carries on.
        /// </summary>
        public bool TopplesOverEdge;

        /// <summary>Starting velocity. Only read when <see cref="TopplesOverEdge"/> is not set.</summary>
        public Vector3 Velocity;

        /// <summary>How far the cap turns over the rim, in degrees, before it lets go of it.</summary>
        public float ToppleAngle;

        /// <summary>Seconds the topple takes.</summary>
        public float ToppleDuration;

        /// <summary>Tumble speed (radians/second) kept up during the free fall.</summary>
        public float Spin;

        public float GravityScale;
        public float VanishHeight;
        public float MaximumLifetime;
    }

    private CapFieldBoundary _owner;
    private Settings _settings;
    private float _remainingLifetime;

    // Axis the cap turns around, perpendicular to the edge it crossed. Shared by both beats, so the
    // tumble during the free fall continues the direction the topple started in.
    private Vector3 _toppleAxis;
    private float _toppleDuration;
    private float _toppleElapsed;

    // How much of the topple has already been applied to the transform. RotateAround is relative, so
    // the animation has to hand it the difference rather than the absolute angle.
    private float _appliedAngle;

    private Vector3 _velocity;

    /// <summary>Starts the fall. The cap stops taking part in the game and in physics.</summary>
    public static void Begin(Cap cap, CapFieldBoundary owner, in Settings settings)
    {
        if (cap == null || cap.GetComponent<FallingCap>() != null) return;

        GameObject capObject = cap.gameObject;
        cap.LeaveGame();
        cap.enabled = false;

        // Nothing about the fall goes through physics, so the cap leaves it altogether: its collider
        // would otherwise keep answering the overlap and raycast queries the aim rules run, and keep
        // being something the caps still in play could touch.
        Collider ownCollider = capObject.GetComponent<Collider>();
        if (ownCollider != null) ownCollider.enabled = false;

        Vector3 outward = settings.OutwardDirection.sqrMagnitude > 0.000001f
            ? settings.OutwardDirection.normalized
            : Vector3.forward;

        // Turning this way sends the outer edge downwards, so the cap rolls over the border instead of
        // sliding off it flat.
        Vector3 axis = Vector3.Cross(Vector3.up, outward);

        FallingCap falling = capObject.AddComponent<FallingCap>();
        falling._owner = owner;
        falling._settings = settings;
        falling._toppleAxis = axis.sqrMagnitude > 0.000001f ? axis.normalized : Vector3.right;
        falling._toppleDuration = settings.TopplesOverEdge ? Mathf.Max(0f, settings.ToppleDuration) : 0f;
        falling._velocity = settings.TopplesOverEdge ? Vector3.zero : settings.Velocity;
        falling._remainingLifetime = settings.MaximumLifetime;
    }

    void Update()
    {
        float deltaTime = Time.deltaTime;
        if (deltaTime > 0f)
        {
            if (_toppleElapsed < _toppleDuration)
                StepTopple(deltaTime);
            else
                StepFall(deltaTime);
        }

        if (transform.position.y <= _settings.VanishHeight)
        {
            if (_owner != null) _owner.ReportFallingCapVanished(transform.position);
            Destroy(gameObject);
            return;
        }

        // Safety net for a cap that never reaches the vanish height. Getting here means the fall itself
        // failed, so it is worth a line in the console rather than a cap that quietly blinks out of
        // existence. The vanish is still reported: this is otherwise the one path where a cap
        // disappears with no sound and no VFX at all.
        _remainingLifetime -= deltaTime;
        if (_remainingLifetime <= 0f)
        {
            Debug.LogWarning(
                $"[FallingCap] {name} never got below y={_settings.VanishHeight} within " +
                $"{_settings.MaximumLifetime} s and is removed at {transform.position}.", this);

            if (_owner != null) _owner.ReportFallingCapVanished(transform.position);
            Destroy(gameObject);
        }
    }

    /// <summary>
    /// Turns the cap over the rim it is balanced on. Squaring the progress gives a constant angular
    /// acceleration, which is what toppling looks like — a linear turn reads as the cap being pushed.
    /// </summary>
    void StepTopple(float deltaTime)
    {
        Vector3 positionBefore = transform.position;

        _toppleElapsed = Mathf.Min(_toppleElapsed + deltaTime, _toppleDuration);
        float progress = _toppleDuration > 0f ? _toppleElapsed / _toppleDuration : 1f;

        float angle = _settings.ToppleAngle * progress * progress;
        transform.RotateAround(_settings.Pivot, _toppleAxis, angle - _appliedAngle);
        _appliedAngle = angle;

        // The speed the topple built up carries into the free fall, so the two beats join without a
        // seam and the cap keeps moving away from the table instead of dropping straight down.
        _velocity = (transform.position - positionBefore) / deltaTime;
    }

    void StepFall(float deltaTime)
    {
        _velocity += Physics.gravity * _settings.GravityScale * deltaTime;
        transform.position += _velocity * deltaTime;
        transform.rotation =
            Quaternion.AngleAxis(_settings.Spin * Mathf.Rad2Deg * deltaTime, _toppleAxis)
            * transform.rotation;
    }
}
