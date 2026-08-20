using UnityEngine;

/// <summary>
/// Interface for cap abilities that display a sticker (icon) above the cap.
/// Each ability provides a sticker sprite and a human-readable description.
/// Implemented by BombCapFlipEffect, FlipperCapEffect, DefenderCapEffect,
/// PredictorCapEffect, and any future ability.
/// </summary>
public interface ICapAbility
{
    /// <summary>Icon sprite displayed as a sticker above the cap.</summary>
    Sprite StickerSprite { get; }

    /// <summary>
    /// Human-readable description of what the ability does.
    /// Example: "When landing Heads-up, pushes all caps away (radius = 3)"
    /// </summary>
    string Description { get; }
}
