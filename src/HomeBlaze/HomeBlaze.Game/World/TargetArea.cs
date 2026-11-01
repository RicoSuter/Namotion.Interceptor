using HomeBlaze.Game.Abstractions;

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
}
