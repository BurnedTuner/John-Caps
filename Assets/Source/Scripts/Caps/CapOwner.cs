/// <summary>
/// Identifies which side owns a cap for scoring purposes.
/// Neutral caps participate in physics and chain reactions but do not affect the score.
/// </summary>
public enum CapOwner
{
    Neutral = 0,
    Player = 1,
    Opponent = 2
}
