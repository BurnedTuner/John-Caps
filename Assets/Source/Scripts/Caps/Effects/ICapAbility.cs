using UnityEngine;

/// <summary>
/// Interface for cap abilities that display a sticker (icon) above the cap.
/// Each ability provides a sticker sprite, a human-readable description,
/// and a level (1-3) that scales its parameters.
/// </summary>
public interface ICapAbility
{
    /// <summary>Icon sprite displayed as a sticker above the cap.</summary>
    Sprite StickerSprite { get; }

    /// <summary>
    /// Human-readable description of what the ability does at its current level.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Ability level (1, 2, or 3). Higher levels = stronger parameters.
    /// The sticker shows an x2/x3 badge for levels 2 and 3.
    /// </summary>
    int Level { get; }
}
