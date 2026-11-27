using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.Simulation;

namespace HomeBlaze.Game.World;

internal static class TargetArea
{
    /// <summary>
    /// Adds the tiles of the area shifted one tile toward <paramref name="facing"/> that the area itself does
    /// not cover, row by row from south-west; adds none when the shifted area would leave the <see cref="int"/> range.
    /// </summary>
    public static void GetTiles(GameArea area, GameDirection facing, List<GameArea> tiles)
    {
        if (!area.TryMove(facing.GetOffsetX(), facing.GetOffsetY(), out var shifted))
        {
            return;
        }

        for (long y = shifted.Y; y <= shifted.Top; y++)
        {
            for (long x = shifted.X; x <= shifted.Right; x++)
            {
                if (!area.Contains(x, y))
                {
                    tiles.Add(new GameArea((int)x, (int)y, 1, 1));
                }
            }
        }
    }

    /// <summary>
    /// Adds the visible entities other than the player that cover a target tile of the player to
    /// <paramref name="results"/>, which must be empty, from south-west to north-east; <paramref name="tiles"/> is
    /// scratch space.
    /// </summary>
    public static void GetTargets(GameWorldIndex index, PlayerEntity player, List<GameArea> tiles, List<GameEntity> results)
    {
        var area = player.Area;
        tiles.Clear();
        GetTiles(area, player.Facing, tiles);
        if (tiles.Count == 0 || !area.TryMove(player.Facing.GetOffsetX(), player.Facing.GetOffsetY(), out var shifted))
        {
            return;
        }

        index.Query(shifted, results);
        for (var i = results.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(results[i], player) || !IntersectsAny(results[i].Area, tiles))
            {
                results.RemoveAt(i);
            }
        }

        results.Sort(static (first, second) =>
            first.Area.Y != second.Area.Y ? first.Area.Y.CompareTo(second.Area.Y) : first.Area.X.CompareTo(second.Area.X));
    }

    private static bool IntersectsAny(GameArea area, List<GameArea> tiles)
    {
        foreach (var tile in tiles)
        {
            if (area.Intersects(tile))
            {
                return true;
            }
        }

        return false;
    }
}
