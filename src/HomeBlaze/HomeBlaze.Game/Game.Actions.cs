using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.Game.World;

namespace HomeBlaze.Game;

public partial class Game
{
    /// <summary>
    /// The time a player waits between two messages it sends.
    /// </summary>
    internal static readonly TimeSpan MessageInterval = TimeSpan.FromSeconds(1);

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

        await WriteOrRequestSaveAsync(cancellationToken);
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

        await WriteOrRequestSaveAsync(cancellationToken);
    }

    /// <summary>
    /// Opens or closes the door and saves the game.
    /// </summary>
    /// <exception cref="InvalidOperationException">The door is not part of this game or already in that state, or a
    /// blocking object stands in it while closing.</exception>
    internal async Task SetDoorOpenAsync(Door door, bool isOpen, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (FindObjectName(CurrentObjects, door) is null)
            {
                throw new InvalidOperationException("The door is not part of this game.");
            }

            if (door.IsOpen == isOpen)
            {
                throw new InvalidOperationException(isOpen ? "The door is already open." : "The door is already closed.");
            }

            var index = GetIndex();
            if (!isOpen && index.IsBlocked(door.Footprint, door))
            {
                throw new InvalidOperationException("Someone is in the way of the door.");
            }

            door.IsOpen = isOpen;
            index.AddOrUpdate(door);
        }

        await WriteOrRequestSaveAsync(cancellationToken);
    }

    /// <exception cref="InvalidOperationException">The sender sent a message less than <see cref="MessageInterval"/> ago.</exception>
    internal Task SendMessageAsync(GamePlayer recipient, GamePlayer? sender, string message, CancellationToken cancellationToken)
    {
        if (sender is not null)
        {
            TakeMessageSlot(sender);
        }

        var notification = new Notification
        {
            Title = sender is null ? "Message" : $"Message from {sender.Title}",
            Message = message
        };
        return _clients.SendNotificationAsync(recipient.ClientId, notification, cancellationToken);
    }

    private void TakeMessageSlot(GamePlayer sender)
    {
        lock (_lock)
        {
            var now = _timeProvider.GetTimestamp();
            if (sender.LastMessageTimestamp is { } last && _timeProvider.GetElapsedTime(last, now) < MessageInterval)
            {
                throw new InvalidOperationException("Wait a moment before sending another message.");
            }

            sender.LastMessageTimestamp = now;
        }
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
