using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Immutable snapshot of a single sticker (one ICapAbility) on a cap, captured
/// at the moment the match ends. Stored on a <see cref="CapSnapshot"/> so the
/// match-rewards UI can render stickers + tooltips WITHOUT depending on the
/// lifetime of the original Cap GameObject (which may be destroyed when the
/// next scene loads).
/// </summary>
public readonly struct StickerSnapshot
{
    /// <summary>Sticker sprite (icon). May be null if the ability has no sticker.</summary>
    public readonly Sprite Sprite;

    /// <summary>Human-readable description of the ability at its current level.</summary>
    public readonly string Description;

    /// <summary>Ability level (1, 2, or 3). Drives the x2/x3 badge.</summary>
    public readonly int Level;

    public StickerSnapshot(Sprite sprite, string description, int level)
    {
        Sprite = sprite;
        Description = description ?? string.Empty;
        Level = level;
    }
}

/// <summary>
/// Immutable snapshot of a cap's visual + ability data, captured at the moment
/// the match ends. Used by <see cref="MatchRewardsPanel"/> to render gained/lost
/// caps. The original Cap GameObject may be destroyed on scene unload; this
/// snapshot is a plain C# struct that survives scene transitions.
/// </summary>
public readonly struct CapSnapshot
{
    /// <summary>
    /// The sprite shown as the cap's icon in the rewards panel. Uses the cap's
    /// DeckSprite (which prefers the GeneratedFaceSprite from CapVisualGenerator,
    /// falling back to the inspector-assigned _deckSprite, then the first sticker).
    /// </summary>
    public readonly Sprite IconSprite;

    /// <summary>Generated back sprite (from CapVisualGenerator), if any.</summary>
    public readonly Sprite BackSprite;

    /// <summary>Cap display name (prefab name). Used for debugging.</summary>
    public readonly string DisplayName;

    /// <summary>Stickers (one per ICapAbility on the cap).</summary>
    public readonly IReadOnlyList<StickerSnapshot> Stickers;

    public CapSnapshot(Sprite icon, Sprite back, string displayName, IReadOnlyList<StickerSnapshot> stickers)
    {
        IconSprite = icon;
        BackSprite = back;
        DisplayName = displayName ?? string.Empty;
        Stickers = stickers ?? System.Array.Empty<StickerSnapshot>();
    }
}

/// <summary>
/// Result of a single battle, captured at the moment the match ends and passed
/// to the UI via <see cref="RunManager.OnBattleEnded"/>. Carries the winner,
/// reason, hearts/level info, and immutable snapshots of the caps the player
/// lost and gained during this battle.
///
/// The cap snapshots are safe to hold across frames — they don't reference
/// the original Cap GameObjects, so they remain valid even after scene unload.
/// </summary>
public class BattleResult
{
    /// <summary>Who won this battle.</summary>
    public CapOwner Winner;

    /// <summary>Why the match ended (kill target, wipeout, no caps, draw).</summary>
    public MatchEndReason Reason;

    /// <summary>True if this was a boss-level battle.</summary>
    public bool IsBoss;

    /// <summary>Hearts remaining AFTER this battle's loss was applied.</summary>
    public int HeartsRemaining;

    /// <summary>Current level index (0-based), BEFORE advancing to the next.</summary>
    public int CurrentLevel;

    /// <summary>Total levels in the run.</summary>
    public int TotalLevels;

    /// <summary>Caps the player LOST during this battle (knocked off the field).</summary>
    public IReadOnlyList<CapSnapshot> LostCaps;

    /// <summary>Caps the player GAINED during this battle (enemy caps knocked off).</summary>
    public IReadOnlyList<CapSnapshot> GainedCaps;

    /// <summary>True if the player won this battle.</summary>
    public bool PlayerWon => Winner == CapOwner.Player;

    /// <summary>True if this was the final level of the run (regardless of outcome).</summary>
    public bool WasLastLevel => CurrentLevel + 1 >= TotalLevels;

    public BattleResult()
    {
        LostCaps = System.Array.Empty<CapSnapshot>();
        GainedCaps = System.Array.Empty<CapSnapshot>();
    }
}
