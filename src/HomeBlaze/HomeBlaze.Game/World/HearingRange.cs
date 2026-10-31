namespace HomeBlaze.Game.World;

/// <summary>
/// The only definition of who hears whom and how loud. Sound travels along the shortest open path between the
/// footprint centers: the straight line while it touches no tile of an object whose
/// <see cref="GameObject.IsSoundBlocking"/> is true, otherwise the path that <see cref="SoundPathSearch"/> finds around
/// such objects. The volume is the distance gain of the path length times the occlusion gain of the detour, the length
/// the path exceeds the straight distance by.
/// </summary>
internal static class HearingRange
{
    /// <summary>
    /// The distance in tiles at which a voice has faded to silence, 10 m.
    /// </summary>
    public const int HearingRadius = 40;

    /// <summary>
    /// The distance in tiles up to which a voice plays at full volume, 1 m; beyond it the volume falls with the inverse
    /// of the distance.
    /// </summary>
    public const int ReferenceDistance = 4;

    /// <summary>
    /// The distance in tiles before <see cref="HearingRadius"/> over which the volume fades smoothly to silence, 2 m.
    /// </summary>
    public const int EdgeFade = 8;

    /// <summary>
    /// The detour in tiles at which the volume is halved, 1 m.
    /// </summary>
    public const int HalvingDetour = 4;

    /// <summary>
    /// Gets the volume from 0 to 1 at which the players hear each other; the same for both orders.
    /// </summary>
    /// <remarks>
    /// When the straight line is blocked, <paramref name="search"/> searches from the player with the ordinally smaller
    /// client id, so the pairs of one player computed one after the other reuse its search.
    /// </remarks>
    public static double GetVolume(GamePlayer first, GamePlayer second, GameWorldIndex index, SoundPathSearch search)
    {
        var firstFootprint = first.Footprint;
        var secondFootprint = second.Footprint;
        var start = GetDoubledCenter(firstFootprint);
        var end = GetDoubledCenter(secondFootprint);
        double deltaX = end.X - start.X;
        double deltaY = end.Y - start.Y;
        var distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY) / 2;
        if (distance >= HearingRadius)
        {
            return 0;
        }

        if (!IsLineBlocked(start, end, firstFootprint, secondFootprint, index))
        {
            return GetDistanceGain(distance);
        }

        // From one fixed end, so both orders get the same length also where a footprint overlaps a sound blocking
        // object, whose tiles count as open only in the source footprint.
        var isFirstSource = IsSearchedFrom(first, second);
        search.Prepare(isFirstSource ? firstFootprint : secondFootprint, index);
        var length = search.GetLength(isFirstSource ? secondFootprint : firstFootprint);
        if (length == SoundPathSearch.NoPath)
        {
            return 0;
        }

        // Against the grid length without walls, so the excess of grid lengths over straight ones cancels out.
        var openLength = SoundPathSearch.GetOpenLength(firstFootprint, secondFootprint);
        var detour = Math.Max(0, length - openLength) / (double)SoundPathSearch.Unit;
        return GetDistanceGain(distance + detour) * GetOcclusionGain(detour);
    }

    /// <summary>
    /// Gets the gain of a path length in tiles: 1 up to <see cref="ReferenceDistance"/>, then the reference distance
    /// divided by the length, times a smooth fade over the last <see cref="EdgeFade"/> tiles to 0 at
    /// <see cref="HearingRadius"/>.
    /// </summary>
    public static double GetDistanceGain(double distance)
    {
        if (distance >= HearingRadius)
        {
            return 0;
        }

        var fade = Math.Min(1, (HearingRadius - distance) / EdgeFade);
        var smoothFade = fade * fade * (3 - 2 * fade);
        return distance <= ReferenceDistance ? smoothFade : ReferenceDistance / distance * smoothFade;
    }

    /// <summary>
    /// Gets the gain of a detour in tiles, 1 / (1 + detour / <see cref="HalvingDetour"/>), which halves the volume at a
    /// detour of <see cref="HalvingDetour"/>.
    /// </summary>
    public static double GetOcclusionGain(double detour) => 1 / (1 + detour / HalvingDetour);

    private static bool IsSearchedFrom(GamePlayer first, GamePlayer second)
    {
        var order = string.CompareOrdinal(first.ClientId, second.ClientId);
        if (order != 0)
        {
            return order < 0;
        }

        var firstFootprint = first.Footprint;
        var secondFootprint = second.Footprint;
        return (firstFootprint.X, firstFootprint.Y, firstFootprint.Width, firstFootprint.Depth)
            .CompareTo((secondFootprint.X, secondFootprint.Y, secondFootprint.Width, secondFootprint.Depth)) <= 0;
    }

    /// <summary>
    /// Gets the area that every footprint of a player who may hear a player with this footprint intersects.
    /// </summary>
    /// <remarks>
    /// The footprint must have a width and depth from 1 to <see cref="GameObject.MaximumSize"/>; the index holds no others.
    /// </remarks>
    public static Footprint GetArea(Footprint footprint)
    {
        var minimumX = Math.Max(int.MinValue, (long)footprint.X - HearingRadius);
        var maximumX = Math.Min(int.MaxValue, footprint.Right + HearingRadius);
        var minimumY = Math.Max(int.MinValue, (long)footprint.Y - HearingRadius);
        var maximumY = Math.Min(int.MaxValue, footprint.Top + HearingRadius);
        return new Footprint(
            (int)minimumX,
            (int)minimumY,
            (int)Math.Min(int.MaxValue, maximumX - minimumX + 1),
            (int)Math.Min(int.MaxValue, maximumY - minimumY + 1));
    }

    // In half tiles, so the center of every footprint is a whole number and the walk stays exact.
    private static (long X, long Y) GetDoubledCenter(Footprint footprint) =>
        (2L * footprint.X + footprint.Width, 2L * footprint.Y + footprint.Depth);

    // Walks every tile that the closed segment touches, also only at an edge or a corner, so sound slips neither
    // between two walls that touch at a corner nor along the face of a wall. Called only within the hearing radius,
    // which keeps the products in GetTouchedRows far inside the long range.
    private static bool IsLineBlocked((long X, long Y) start, (long X, long Y) end, Footprint first, Footprint second, GameWorldIndex index)
    {
        var lastColumn = FloorDivide(Math.Max(start.X, end.X), 2);
        for (var column = CeilingDivide(Math.Min(start.X, end.X), 2) - 1; column <= lastColumn; column++)
        {
            var (firstRow, lastRow) = GetTouchedRows(column, start, end);
            for (var row = firstRow; row <= lastRow; row++)
            {
                if (!first.Contains(column, row) && !second.Contains(column, row) && index.IsSoundBlocked(column, row))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // The rows of the column whose closed tiles the segment touches; a tile spans 2 half tiles.
    private static (long First, long Last) GetTouchedRows(long column, (long X, long Y) start, (long X, long Y) end)
    {
        var deltaX = end.X - start.X;
        if (deltaX == 0)
        {
            return (CeilingDivide(Math.Min(start.Y, end.Y), 2) - 1, FloorDivide(Math.Max(start.Y, end.Y), 2));
        }

        // Where the segment enters and leaves the column, as heights over a positive denominator.
        var deltaY = end.Y - start.Y;
        var sign = Math.Sign(deltaX);
        var left = Math.Max(2 * column, Math.Min(start.X, end.X));
        var right = Math.Min(2 * column + 2, Math.Max(start.X, end.X));
        var atLeft = (start.Y * deltaX + (left - start.X) * deltaY) * sign;
        var atRight = (start.Y * deltaX + (right - start.X) * deltaY) * sign;
        var denominator = 2 * deltaX * sign;
        return (CeilingDivide(Math.Min(atLeft, atRight), denominator) - 1, FloorDivide(Math.Max(atLeft, atRight), denominator));
    }

    // Rounds toward negative infinity for a positive denominator.
    private static long FloorDivide(long numerator, long denominator) =>
        numerator >= 0 ? numerator / denominator : -((denominator - 1 - numerator) / denominator);

    private static long CeilingDivide(long numerator, long denominator) => -FloorDivide(-numerator, denominator);
}
