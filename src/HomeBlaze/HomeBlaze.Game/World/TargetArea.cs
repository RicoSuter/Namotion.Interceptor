namespace HomeBlaze.Game.World;

internal static class TargetArea
{
    /// <summary>
    /// Adds the tiles of the footprint shifted one tile toward <paramref name="facing"/> that the footprint itself does
    /// not cover, row by row from south-west; adds none when the shifted footprint would leave the <see cref="int"/> range.
    /// </summary>
    public static void GetTiles(Footprint footprint, GameDirection facing, List<Footprint> tiles)
    {
        if (!footprint.TryMove(facing.GetOffsetX(), facing.GetOffsetY(), out var shifted))
        {
            return;
        }

        for (long y = shifted.Y; y <= shifted.Top; y++)
        {
            for (long x = shifted.X; x <= shifted.Right; x++)
            {
                if (!footprint.Contains(x, y))
                {
                    tiles.Add(new Footprint((int)x, (int)y, 1, 1));
                }
            }
        }
    }
}
