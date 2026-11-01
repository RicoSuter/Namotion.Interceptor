namespace HomeBlaze.Game.Simulation;

/// <summary>
/// Thrown for work given to a game whose simulation has stopped.
/// </summary>
internal sealed class GameStoppedException : InvalidOperationException
{
    public GameStoppedException()
        : base("The game has stopped.")
    {
    }
}
