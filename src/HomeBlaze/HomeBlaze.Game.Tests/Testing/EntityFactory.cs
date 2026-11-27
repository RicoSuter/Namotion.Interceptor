using HomeBlaze.Game.Simulation;

namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// Creates entities as the simulation holds them, for tests of the parts that work on entities.
/// </summary>
internal static class EntityFactory
{
    /// <summary>
    /// Creates a connected player of the client at the position.
    /// </summary>
    public static PlayerEntity Player(string clientId, int x, int y)
    {
        var player = new PlayerEntity(new GamePlayer(), clientId) { Area = new GameArea(x, y, GamePlayer.Size, GamePlayer.Size) };
        player.SetConnected(true);
        return player;
    }

    /// <summary>
    /// Creates the entity of the object, read from it.
    /// </summary>
    public static GameEntity Object(GameObject subject)
    {
        var entity = new GameEntity(subject);
        entity.ReadFromSubject();
        return entity;
    }
}
