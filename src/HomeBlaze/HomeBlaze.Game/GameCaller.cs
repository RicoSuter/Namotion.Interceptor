namespace HomeBlaze.Game;

/// <summary>
/// Who invokes an operation of the game; operations take it as a nullable [FromServices] parameter, which only the game
/// page provides. <see cref="Game.ResolveActor"/> turns it into the acting player.
/// </summary>
public sealed class GameCaller
{
    public GameCaller(GamePlayer player)
    {
        Player = player;
    }

    /// <summary>
    /// The player of the page that invokes the operation.
    /// </summary>
    public GamePlayer Player { get; }
}
