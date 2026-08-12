using UnityEngine;

/// <summary>
/// Everything the opponent AI weighs when it picks a throw. Serialized inside AiCapThrower so a
/// designer can retune the personality without touching code.
///
/// The weights encode the extra-turn rule: knocking an enemy cap off the field keeps the turn, so a
/// knockout is worth far more than any positional gain, and the caps the AI leaves exposed matter
/// much less on a turn the player never gets to answer.
/// </summary>
[System.Serializable]
public sealed class AiSearchSettings
{
    [Header("Scoring weights")]
    [Tooltip("Value of every player cap driven off the field.")]
    public float KillWeight = 10f;

    [Tooltip("Flat bonus for knocking off at least one player cap, because that keeps the turn. " +
             "Deliberately larger than KillWeight: holding the initiative beats any positional gain.")]
    public float ExtraTurnBonus = 25f;

    [Tooltip("Cost of losing one of the AI's own caps. Above KillWeight, so a plain one-for-one " +
             "trade is a loss — it only pays off through the extra turn that follows.")]
    public float SelfLossWeight = 12f;

    [Tooltip("Cost of knocking a neutral cap off. Small: neutral caps grant no extra turn.")]
    public float NeutralLossWeight = 0.5f;

    [Tooltip("Cost per unit of exposure of the AI's surviving caps.")]
    public float OwnDangerWeight = 2f;

    [Tooltip("How much own-cap exposure still counts when the move keeps the turn. " +
             "The player does not get to punish it, so it is mostly discounted.")]
    [Range(0f, 1f)] public float ExtraTurnDangerDiscount = 0.25f;

    [Tooltip("Reward per unit of exposure left on the player's surviving caps — easy targets for later.")]
    public float EnemyDangerWeight = 1f;

    [Tooltip("Cost of the AI's own cap being absorbed into a stack, which takes it out of play.")]
    public float OwnStackedWeight = 1f;

    [Tooltip("Score for a move that clears the last player cap off the board.")]
    public float WinScore = 10000f;

    [Header("Chain lookahead")]
    [Tooltip("How many links of the chain the AI foresees. 0 = the whole chain, bounded only by " +
             "CapTuning.MaximumChainLength. 1 = only the caps the throw hits directly; they still fly " +
             "and can still leave the field, they just knock nothing else over. 2 = one link further, " +
             "and so on. Lowering it makes the AI blind to long cascades, which is a way to weaken it.")]
    [Min(0)] public int MaxChainDepth;

    [Header("Candidate sampling")]
    [Tooltip("Landing points sampled on a ring around each target cap.")]
    [Range(4, 32)] public int RingAngles = 12;

    [Tooltip("Ring radii as a fraction of the combined radius. 0.95 is a grazing hit, which the " +
             "engine's contact-factor formula turns into the longest knockback.")]
    public float[] RingOffsets = { 0.95f, 0.75f, 0.5f };

    [Tooltip("Spacing of the fallback grid swept over the field. 0 disables the grid.")]
    [Min(0f)] public float GridStep = 2f;

    [Tooltip("Landing points closer than this to the field edge are rejected, so the AI never " +
             "throws its own cap into an obviously lost position.")]
    [Min(0f)] public float LandingEdgeMargin = 0.5f;

    [Tooltip("Also sample around neutral caps — useful for chains and for setting off bombs.")]
    public bool TargetNeutralCaps = true;

    [Tooltip("Also sample around the AI's own caps. Off by default: usually a way to lose them.")]
    public bool TargetOwnCaps = false;

    [Tooltip("Safety valve on how many landing points are evaluated in one turn.")]
    [Min(16)] public int MaxCandidates = 1024;

    [Tooltip("Landing points are snapped to this grid before evaluation to drop near-duplicates.")]
    [Min(0.05f)] public float DeduplicationStep = 0.25f;

    [Header("Difficulty")]
    [Tooltip("Pick uniformly among the N best moves. 1 = always the best one.")]
    [Min(1)] public int TopNChoices = 1;

    [Tooltip("Random offset applied to the landing point AFTER the move is chosen, in world units. " +
             "0 = a perfectly steady hand. Applied afterwards on purpose: it models a shaky throw, " +
             "so the outcome honestly differs from what the AI evaluated.")]
    [Min(0f)] public float AimJitter = 0f;

    [Header("Player model")]
    [Tooltip("Throw power the danger metric assumes for the player. 0 = read it from the player's cap prefab.")]
    [Min(0f)] public float PlayerThrowPowerOverride = 0f;

    [Header("Debug")]
    [Tooltip("Log the five best moves with their score broken down term by term.")]
    public bool VerboseLog = false;

    [Tooltip("Draw the evaluated landing points as gizmos, coloured by score.")]
    public bool DrawGizmos = false;
}
