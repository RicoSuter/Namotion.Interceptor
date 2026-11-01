using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.World;

namespace HomeBlaze.Game;

public partial class Game
{
    /// <summary>
    /// The message with which placing is refused for a caller that is not a player of this game.
    /// </summary>
    internal const string NoActorMessage = "Only a player in the game can place objects.";

    /// <summary>
    /// The message with which placing in front of a player that faces diagonally is refused.
    /// </summary>
    internal const string DiagonalMessage = "Face north, east, south or west to place objects.";

    /// <inheritdoc />
    public GameSpot GetSpotInFront(GameCaller? caller, int across, int along)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(across, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(across, GameObject.MaximumSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(along, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(along, GameObject.MaximumSize);
        lock (_lock)
        {
            var actor = GetActor(caller) ?? throw new InvalidOperationException(NoActorMessage);
            if (!FrontPlacement.TryGetArea(actor.Area, actor.Facing, across, along, out var area))
            {
                throw new InvalidOperationException(!Enum.IsDefined(actor.Facing) || actor.Facing.IsDiagonal() ? DiagonalMessage : BlockedMessage);
            }

            return new GameSpot(area.X, area.Y, area.Width, area.Depth, actor.Facing);
        }
    }

    /// <inheritdoc />
    public bool IsFree(GameArea area, GameObject? except = null)
    {
        lock (_lock)
        {
            return !GetIndex().IsBlocked(area, except);
        }
    }

    /// <inheritdoc />
    public async Task PlaceAsync(GameObject gameObject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gameObject);
        lock (_lock)
        {
            var objects = CurrentObjects;
            ValidateNewObject(objects, gameObject, nameof(gameObject));
            var index = GetIndex();
            if (gameObject.IsMovementBlocking && index.IsBlocked(gameObject.Area, ignored: null))
            {
                throw new InvalidOperationException(BlockedMessage);
            }

            ReplaceObjects(new Dictionary<string, GameObject>(objects) { [CreateObjectName(objects, gameObject)] = gameObject });
            index.AddOrUpdate(gameObject);
        }

        await WriteOrRequestSaveAsync(cancellationToken);
    }
}
