namespace HomeBlaze.Game.Abstractions;

/// <summary>
/// The refusal texts that the game and object types share, so a player reads the same message for the same reason.
/// </summary>
public static class GameMessages
{
    /// <summary>
    /// A change is refused because a movement blocking object is in the way.
    /// </summary>
    public const string Blocked = "Something is in the way.";
}
