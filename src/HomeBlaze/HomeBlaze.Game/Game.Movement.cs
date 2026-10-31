using HomeBlaze.Game.World;

namespace HomeBlaze.Game;

/// <summary>
/// The verdict on a step and the position and facing of the player after it.
/// </summary>
internal readonly record struct MoveResult(bool IsAccepted, int X, int Y, GameDirection Facing);

public partial class Game
{
    /// <summary>
    /// Turns the player toward <paramref name="direction"/> and, unless <paramref name="turnOnly"/>, steps one tile that
    /// way when the step is allowed; the caller must pass only the player of its own client.
    /// </summary>
    internal MoveResult TryMove(GamePlayer player, GameDirection direction, bool turnOnly)
    {
        lock (_lock)
        {
            if (!IsPlayerOfThisGame(player) || !Enum.IsDefined(direction))
            {
                return new MoveResult(false, player.X, player.Y, player.Facing);
            }

            if (!turnOnly)
            {
                player.RateLimiter ??= new StepRateLimiter(_timeProvider);
                if (!player.RateLimiter.TryTake())
                {
                    return new MoveResult(false, player.X, player.Y, player.Facing);
                }
            }

            player.Facing = direction;
            _isSaveRequested = true;
            if (turnOnly || !TryGetStep(player, direction, out var moved))
            {
                return new MoveResult(turnOnly, player.X, player.Y, player.Facing);
            }

            player.X = moved.X;
            player.Y = moved.Y;
            GetIndex().AddOrUpdate(player);
            return new MoveResult(true, player.X, player.Y, player.Facing);
        }
    }

    /// <summary>
    /// Gets the visible objects other than the player in the tiles in front of it, from south-west to north-east.
    /// </summary>
    internal IReadOnlyList<GameObject> GetTargetObjects(GamePlayer player)
    {
        lock (_lock)
        {
            var tiles = new List<Footprint>(3);
            TargetArea.GetTiles(player.Footprint, player.Facing, tiles);
            if (tiles.Count == 0 || !player.Footprint.TryMove(player.Facing.GetOffsetX(), player.Facing.GetOffsetY(), out var shifted))
            {
                return [];
            }

            var candidates = new List<GameObject>();
            GetIndex().Query(shifted, candidates);
            return candidates
                .Where(candidate => !ReferenceEquals(candidate, player) && tiles.Exists(tile => candidate.Footprint.Intersects(tile)))
                .OrderBy(candidate => candidate.Y)
                .ThenBy(candidate => candidate.X)
                .ToList();
        }
    }

    // Callers hold _lock.
    private bool IsPlayerOfThisGame(GamePlayer player) =>
        Players.TryGetValue(player.ClientId, out var current) && ReferenceEquals(current, player);

    // Callers hold _lock.
    private bool TryGetStep(GamePlayer player, GameDirection direction, out Footprint moved)
    {
        var index = GetIndex();
        var footprint = player.Footprint;
        var offsetX = direction.GetOffsetX();
        var offsetY = direction.GetOffsetY();
        if (!footprint.TryMove(offsetX, offsetY, out moved) || index.IsBlocked(moved, player))
        {
            return false;
        }

        if (!direction.IsDiagonal())
        {
            return true;
        }

        var isHorizontalBlocked = !footprint.TryMove(offsetX, 0, out var horizontal) || index.IsBlocked(horizontal, player);
        var isVerticalBlocked = !footprint.TryMove(0, offsetY, out var vertical) || index.IsBlocked(vertical, player);
        return !(isHorizontalBlocked && isVerticalBlocked);
    }
}
