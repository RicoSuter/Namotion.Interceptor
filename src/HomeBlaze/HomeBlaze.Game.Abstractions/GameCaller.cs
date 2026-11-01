namespace HomeBlaze.Game.Abstractions;

/// <summary>
/// Who invokes an operation of a game object or tool; operations take it as a nullable <c>[FromServices]</c> parameter,
/// which only the game page provides. Null means the operation runs without a player, for example from MCP or an agent.
/// </summary>
public sealed class GameCaller
{
    public GameCaller(string clientId)
    {
        ClientId = clientId;
    }

    /// <summary>
    /// The id of the browser client whose page invokes the operation.
    /// </summary>
    public string ClientId { get; }
}
