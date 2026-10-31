using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.Game.World;

namespace HomeBlaze.Game;

public partial class Game
{
    /// <summary>
    /// Gets the player of this game the caller acts as, or null when it acts as none, for example from the subject page,
    /// MCP or an agent.
    /// </summary>
    public GamePlayer? ResolveActor(GameCaller? caller)
    {
        if (caller is null)
        {
            return null;
        }

        lock (_lock)
        {
            return IsPlayerOfThisGame(caller.Player) ? caller.Player : null;
        }
    }

    [Operation(Title = "Build wall", Icon = "AddBox", Position = 1,
        Description = "Builds a wall segment of 1 m across the direction you face, right in front of you.")]
    public async Task BuildWallAsync([FromServices] GameCaller? caller, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var actor = ResolveActor(caller) ?? throw new InvalidOperationException("Only a player in the game can build a wall.");
            if (!WallPlacement.TryGetFootprint(actor.Footprint, actor.Facing, out var footprint))
            {
                throw new InvalidOperationException("Face north, east, south or west to build a wall.");
            }

            var index = GetIndex();
            if (index.IsBlocked(footprint, ignored: null))
            {
                throw new InvalidOperationException("Something is in the way of the wall.");
            }

            var wall = new Wall { X = footprint.X, Y = footprint.Y, Width = footprint.Width, Depth = footprint.Depth };
            var objects = CurrentObjects;
            ReplaceObjects(new Dictionary<string, GameObject>(objects) { [CreateObjectName(objects, "wall")] = wall });
            index.AddOrUpdate(wall);
        }

        await WriteConfigurationAsync(cancellationToken);
    }

    /// <summary>
    /// Removes the object from <see cref="Objects"/> and saves the game.
    /// </summary>
    /// <exception cref="InvalidOperationException">The object is not part of this game.</exception>
    internal async Task RemoveObjectAsync(GameObject gameObject, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var objects = CurrentObjects;
            var name = FindObjectName(objects, gameObject) ?? throw new InvalidOperationException("The object is not part of this game.");
            var next = new Dictionary<string, GameObject>(objects);
            next.Remove(name);
            ReplaceObjects(next);
            GetIndex().Remove(gameObject);
        }

        await WriteConfigurationAsync(cancellationToken);
    }

    internal Task SendMessageAsync(GamePlayer recipient, GamePlayer? sender, string message, CancellationToken cancellationToken)
    {
        var notification = new Notification
        {
            Title = sender is null ? "Message" : $"Message from {sender.Title}",
            Message = message
        };
        return _clients.SendNotificationAsync(recipient.ClientId, notification, cancellationToken);
    }

    private static string? FindObjectName(Dictionary<string, GameObject> objects, GameObject gameObject)
    {
        foreach (var (name, candidate) in objects)
        {
            if (ReferenceEquals(candidate, gameObject))
            {
                return name;
            }
        }

        return null;
    }

    private static string CreateObjectName(Dictionary<string, GameObject> objects, string prefix)
    {
        string name;
        do
        {
            name = $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 9)];
        }
        while (objects.ContainsKey(name));

        return name;
    }
}
