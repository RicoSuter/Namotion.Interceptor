namespace HomeBlaze.Game.World;

/// <summary>
/// The only definition of who hears whom: players hear each other while their footprint centers are less than
/// <see cref="HearingRadius"/> apart and the straight line between the centers touches no tile that blocks sound.
/// </summary>
internal static class HearingRange
{
    /// <summary>
    /// The distance in tiles at which a voice has faded to silence, 10 m.
    /// </summary>
    public const int HearingRadius = 40;

    /// <summary>
    /// Gets the volume from 0 to 1 at which the players hear each other, falling linearly with the distance between
    /// their centers; the same for both orders.
    /// </summary>
    public static double GetVolume(GamePlayer first, GamePlayer second, GameWorldIndex index)
    {
        if (string.CompareOrdinal(first.ClientId, second.ClientId) > 0)
        {
            (first, second) = (second, first);
        }

        var firstFootprint = first.Footprint;
        var secondFootprint = second.Footprint;
        var start = GetDoubledCenter(firstFootprint);
        var end = GetDoubledCenter(secondFootprint);
        double deltaX = end.X - start.X;
        double deltaY = end.Y - start.Y;
        var distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY) / 2;
        if (distance >= HearingRadius || IsLineBlocked(start, end, firstFootprint, secondFootprint, index))
        {
            return 0;
        }

        return 1 - distance / HearingRadius;
    }

    /// <summary>
    /// Gets the area that every footprint of a player who may hear a player with this footprint intersects.
    /// </summary>
    public static Footprint GetArea(Footprint footprint)
    {
        var minimumX = Math.Max(int.MinValue, (long)footprint.X - HearingRadius);
        var maximumX = Math.Min(int.MaxValue, footprint.Right + HearingRadius);
        var minimumY = Math.Max(int.MinValue, (long)footprint.Y - HearingRadius);
        var maximumY = Math.Min(int.MaxValue, footprint.Top + HearingRadius);
        return new Footprint((int)minimumX, (int)minimumY, (int)(maximumX - minimumX + 1), (int)(maximumY - minimumY + 1));
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
