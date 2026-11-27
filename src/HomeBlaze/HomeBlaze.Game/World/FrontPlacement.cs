using HomeBlaze.Game.Abstractions;

namespace HomeBlaze.Game.World;

internal static class FrontPlacement
{
    /// <summary>
    /// Gets the area of an object <paramref name="across"/> tiles wide across the facing direction and
    /// <paramref name="along"/> tiles deep along it, starting on the tile right in front of the actor and centered on the
    /// actor's center across the facing (an odd remainder goes east or north); returns false for a diagonal facing or a
    /// area outside the <see cref="int"/> range.
    /// </summary>
    public static bool TryGetArea(GameArea actor, GameDirection facing, int across, int along, out GameArea area)
    {
        if (!Enum.IsDefined(facing) || facing.IsDiagonal())
        {
            area = default;
            return false;
        }

        var isNorthOrSouth = facing is GameDirection.North or GameDirection.South;
        var sizeX = isNorthOrSouth ? across : along;
        var sizeY = isNorthOrSouth ? along : across;
        return TryCreate(GetX(actor, facing, along, sizeX), GetY(actor, facing, along, sizeY), sizeX, sizeY, out area);
    }

    private static long GetX(GameArea actor, GameDirection facing, int along, int sizeX) => facing switch
    {
        GameDirection.East => actor.Right + 1,
        GameDirection.West => (long)actor.X - along,
        _ => (long)actor.X + (((long)actor.Width - sizeX + 1) >> 1)
    };

    private static long GetY(GameArea actor, GameDirection facing, int along, int sizeY) => facing switch
    {
        GameDirection.North => actor.Top + 1,
        GameDirection.South => (long)actor.Y - along,
        _ => (long)actor.Y + (((long)actor.Depth - sizeY + 1) >> 1)
    };

    private static bool TryCreate(long x, long y, int sizeX, int sizeY, out GameArea area)
    {
        if (x < int.MinValue || y < int.MinValue || x + sizeX - 1 > int.MaxValue || y + sizeY - 1 > int.MaxValue)
        {
            area = default;
            return false;
        }

        area = new GameArea((int)x, (int)y, sizeX, sizeY);
        return true;
    }
}
