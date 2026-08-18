using UnityEngine;

/// <summary>
/// Interface for cap effects that have a circular radius zone, shown in the
/// trajectory preview. Implemented by BombCapFlipEffect, DefenderCapEffect,
/// and any future radial effect.
///
/// The trajectory preview draws a circle at the effect's center with the
/// effect's radius, colored by the effect's preferred color.
/// </summary>
public interface ICapEffectRadius
{
    /// <summary>The radius of the effect zone (circle drawn in the preview).</summary>
    float EffectRadius { get; }

    /// <summary>
    /// True if the effect should be shown/active given the cap's current side.
    /// For the held cap (throw), this uses the cap's current IsHeads.
    /// For predicted caps (chain), this uses the predicted post-flip side.
    /// </summary>
    bool ShouldTriggerOnSide(bool isHeads);

    /// <summary>
    /// The color to use for the radius circle in the trajectory preview.
    /// Different effects can use different colors (e.g., red for bomb, blue for defender).
    /// </summary>
    Color ZoneColor { get; }
}
