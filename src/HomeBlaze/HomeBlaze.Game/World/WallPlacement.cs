namespace HomeBlaze.Game.World;

internal static class WallPlacement
{
    /// <summary>
    /// Gets the footprint of a default wall right in front of the actor, across its facing direction and centered on it;
    /// returns false for a diagonal facing or a footprint outside the <see cref="int"/> range.
    /// </summary>
    public static bool TryGetFootprint(Footprint actor, GameDirection facing, out Footprint wall)
    {
        long x;
        long y;
        int width;
        int depth;
        switch (facing)
        {
            case GameDirection.North or GameDirection.South:
                width = Wall.DefaultLength;
                depth = 1;
                x = (long)actor.X + (actor.Width - width) / 2;
                y = facing == GameDirection.North ? actor.Top + 1 : (long)actor.Y - 1;
                break;
            case GameDirection.East or GameDirection.West:
                width = 1;
                depth = Wall.DefaultLength;
                x = facing == GameDirection.East ? actor.Right + 1 : (long)actor.X - 1;
                y = (long)actor.Y + (actor.Depth - depth) / 2;
                break;
            default:
                wall = default;
                return false;
        }

        if (x < int.MinValue || y < int.MinValue || x + width - 1 > int.MaxValue || y + depth - 1 > int.MaxValue)
        {
            wall = default;
            return false;
        }

        wall = new Footprint((int)x, (int)y, width, depth);
        return true;
    }
}
