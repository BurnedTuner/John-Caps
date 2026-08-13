using UnityEngine;

/// <summary>
/// The parameters an impact reads off the cap it hits. Both a live <see cref="Cap"/> and a headless
/// copy of one can produce this, which is what lets the runtime resolver and the AI simulation share
/// the formulas below instead of each carrying its own transcription of them.
/// </summary>
public readonly struct CapImpactTarget
{
    public readonly float Radius;
    public readonly float PowerConversion;
    public readonly float CenterContactFactor;
    public readonly float EdgeContactFactor;

    public CapImpactTarget(
        float radius,
        float powerConversion,
        float centerContactFactor,
        float edgeContactFactor)
    {
        Radius = radius;
        PowerConversion = powerConversion;
        CenterContactFactor = centerContactFactor;
        EdgeContactFactor = edgeContactFactor;
    }

    public static CapImpactTarget From(CapParameters parameters) => parameters != null
        ? new CapImpactTarget(
            parameters.Radius,
            parameters.PowerConversion,
            parameters.CenterContactFactor,
            parameters.EdgeContactFactor)
        : new CapImpactTarget(0.5f, 1f, 0f, 1f);

    public float GetContactFactor(float normalizedOffset) =>
        Mathf.Lerp(CenterContactFactor, EdgeContactFactor, Mathf.Clamp01(normalizedOffset));
}

/// <summary>
/// The rules of one cap hitting another, in one place.
///
/// CapTurnResolver drives the live board, ChainPredictor and CapThrower drive the player's aim
/// preview, and CapBoardSimulation drives the AI's search — all four used to carry their own copy of
/// these few lines, so a change to the feel of the game had to be made four times to stay consistent.
/// They now all call in here.
/// </summary>
public static class CapImpact
{
    /// <summary>
    /// Resolves a cap landing at <paramref name="landingPosition"/> against one cap standing at
    /// <paramref name="targetPosition"/>.
    ///
    /// Returns false when the two are too far apart to touch. Returns true for a contact, and then
    /// <paramref name="stacks"/> tells the two outcomes apart: a contact that would move the target
    /// less than CapTuning.MinimumFlightLength does not launch it, it buries the landed cap under it.
    /// Callers that have no notion of stacking simply ignore such a contact.
    /// </summary>
    public static bool TryResolveHit(
        float slammerRadius,
        in CapImpactTarget target,
        Vector2 landingPosition,
        Vector2 targetPosition,
        float landingForce,
        CapTuning tuning,
        out Vector2 direction,
        out float inheritedForce,
        out float travelDistance,
        out bool stacks)
    {
        direction = default;
        inheritedForce = 0f;
        travelDistance = 0f;
        stacks = false;

        if (tuning == null) return false;

        float combinedRadius = slammerRadius + target.Radius;
        float distance = Vector2.Distance(landingPosition, targetPosition);
        if (distance > combinedRadius) return false;

        float normalizedOffset = combinedRadius > 0f
            ? Mathf.Clamp01(distance / combinedRadius)
            : 0f;

        inheritedForce = landingForce * target.PowerConversion;
        travelDistance = inheritedForce * target.GetContactFactor(normalizedOffset) * tuning.ForceToTravelDistance;
        direction = CapMath.VerticalImpactDirection(landingPosition, targetPosition, Vector2.up);
        stacks = travelDistance < tuning.MinimumFlightLength;
        return true;
    }

    /// <summary>
    /// Converts a raw launch force — from a flip effect rather than from a landing — into the motion
    /// it produces. Returns false when the push is too weak to move the cap at all.
    /// </summary>
    public static bool TryResolveLaunch(
        in CapImpactTarget target,
        float rawForce,
        CapTuning tuning,
        out float force,
        out float travelDistance)
    {
        force = 0f;
        travelDistance = 0f;

        if (tuning == null) return false;

        force = rawForce * target.PowerConversion;
        travelDistance = force * tuning.ForceToTravelDistance;

        if (!float.IsFinite(force) || !float.IsFinite(travelDistance)) return false;
        return travelDistance >= tuning.MinimumFlightLength;
    }

    /// <summary>Direction a radial effect pushes a cap, away from the centre of the effect.</summary>
    public static Vector2 RadialDirection(Vector2 origin, Vector2 targetPosition)
    {
        Vector2 offset = targetPosition - origin;
        return offset.sqrMagnitude > 0.000001f ? offset.normalized : Vector2.right;
    }
}
