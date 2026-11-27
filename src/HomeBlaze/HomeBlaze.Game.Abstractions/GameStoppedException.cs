namespace HomeBlaze.Game.Abstractions;

/// <summary>
/// Thrown for a command given to a game whose simulation has stopped.
/// </summary>
public sealed class GameStoppedException : InvalidOperationException
{
    private const string DefaultMessage = "The game has stopped.";

    public GameStoppedException()
        : base(DefaultMessage)
    {
    }

    public GameStoppedException(string? message)
        : base(message ?? DefaultMessage)
    {
    }

    public GameStoppedException(string? message, Exception? innerException)
        : base(message ?? DefaultMessage, innerException)
    {
    }
}
