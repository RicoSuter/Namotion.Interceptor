using HomeBlaze.Game.Tools;
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

    /// <summary>
    /// Gets where an object <paramref name="across"/> tiles wide across the facing direction and <paramref name="along"/>
    /// tiles deep along it fits right in front of the caller's player, centered on the player's center across the facing.
    /// Does not check whether the tiles are free; <see cref="PlaceAsync"/> does.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A size is outside 1 to <see cref="GameObject.MaximumSize"/>.</exception>
    /// <exception cref="InvalidOperationException">The caller is not a player of this game, faces diagonally, or the
    /// footprint would leave the <see cref="int"/> range.</exception>
    public GamePlacement GetPlacementInFront(GameCaller? caller, int across, int along)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(across, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(across, GameObject.MaximumSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(along, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(along, GameObject.MaximumSize);
        lock (_lock)
        {
            var actor = ResolveActor(caller) ?? throw new InvalidOperationException(NoActorMessage);
            if (!FrontPlacement.TryGetFootprint(actor.Footprint, actor.Facing, across, along, out var footprint))
            {
                throw new InvalidOperationException(!Enum.IsDefined(actor.Facing) || actor.Facing.IsDiagonal() ? DiagonalMessage : BlockedMessage);
            }

            return new GamePlacement(footprint.X, footprint.Y, footprint.Width, footprint.Depth, actor.Facing);
        }
    }

    /// <summary>
    /// Adds the object to <see cref="Objects"/> under a new name of the prefix, a dash and 8 random hex digits, and saves
    /// the game.
    /// Canceling only stops the write; the change stays applied and is saved later.
    /// </summary>
    /// <exception cref="ArgumentException">The prefix is empty, the object is already part of this game, or its size is
    /// outside 1 to <see cref="GameObject.MaximumSize"/>.</exception>
    /// <exception cref="InvalidOperationException">The caller is not a player of this game, or the object blocks movement
    /// and another movement blocking object covers one of its tiles.</exception>
    public async Task PlaceAsync(GameCaller? caller, string namePrefix, GameObject gameObject, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(namePrefix);
        ArgumentNullException.ThrowIfNull(gameObject);
        lock (_lock)
        {
            if (ResolveActor(caller) is null)
            {
                throw new InvalidOperationException(NoActorMessage);
            }

            var objects = CurrentObjects;
            ValidateNewObject(objects, gameObject, nameof(gameObject));
            var index = GetIndex();
            if (gameObject.IsMovementBlocking && index.IsBlocked(gameObject.Footprint, ignored: null))
            {
                throw new InvalidOperationException(BlockedMessage);
            }

            ReplaceObjects(new Dictionary<string, GameObject>(objects) { [CreateObjectName(objects, namePrefix)] = gameObject });
            index.AddOrUpdate(gameObject);
        }

        await WriteOrRequestSaveAsync(cancellationToken);
    }
}
