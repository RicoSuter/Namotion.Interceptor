namespace HomeBlaze.Game.World;

internal static class SpawnFinder
{
    /// <summary>
    /// The farthest ring searched, 64 m.
    /// </summary>
    public const int MaximumRadius = 256;

    /// <summary>
    /// Returns the position nearest to <paramref name="start"/>, ring by ring, at which the footprint is free, or start
    /// when none is free within <paramref name="maximumRadius"/>.
    /// </summary>
    public static Footprint FindFreePosition(Footprint start, Func<Footprint, bool> isFree, int maximumRadius = MaximumRadius)
    {
        if (isFree(start))
        {
            return start;
        }

        for (var radius = 1; radius <= maximumRadius; radius++)
        {
            if (TryFindOnRing(start, radius, isFree, out var found))
            {
                return found;
            }
        }

        return start;
    }

    private static bool TryFindOnRing(Footprint start, int radius, Func<Footprint, bool> isFree, out Footprint found)
    {
        for (var offsetY = -radius; offsetY <= radius; offsetY++)
        {
            // Only the ring itself: the full row at its top and bottom, the two ends in between.
            var step = Math.Abs(offsetY) == radius ? 1 : 2 * radius;
            for (var offsetX = -radius; offsetX <= radius; offsetX += step)
            {
                if (start.TryMove(offsetX, offsetY, out found) && isFree(found))
                {
                    return true;
                }
            }
        }

        found = start;
        return false;
    }
}
