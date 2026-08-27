using UnityEngine;

/// <summary>
/// Interface for cap effects that have a circular radius zone, shown in the
/// trajectory preview. Implemented by BombCapFlipEffect, DefenderCapEffect,
/// and any future radial effect.
///
/// The trajectory preview draws a circle at the effect's center with the
/// effect's radius, colored by the effect's preferred color.
///
/// Effects that PUSH caps (e.g., bomb) also expose EffectForce, used by the
/// trajectory preview to predict where affected caps will land after the push.
/// Effects that don't push (e.g., defender, flipper) return 0 for EffectForce.
/// </summary>
public interface ICapEffectRadius
{
    /// <summary>The radius of the effect zone (circle drawn in the preview).</summary>
    float EffectRadius { get; }

    /// <summary>
    /// The force applied to caps inside the radius. Used by the trajectory
    /// preview to compute predicted push distance for affected caps.
    /// Return 0 if the effect doesn't push caps (e.g., defender, flipper).
    /// </summary>
    float EffectForce { get; }

    /// <summary>
    /// True if the effect should be shown/active given the cap's current side.
    /// For the held cap (throw), this uses the cap's current IsFace.
    /// For predicted caps (chain), this uses the predicted post-flip side.
    /// </summary>
    bool ShouldTriggerOnSide(bool isFace);

    /// <summary>
    /// The color to use for the radius circle in the trajectory preview.
    /// Different effects can use different colors (e.g., red for bomb, blue for defender).
    /// </summary>
    Color ZoneColor { get; }
}
