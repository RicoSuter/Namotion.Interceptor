using HomeBlaze.Game.Abstractions;

namespace HomeBlaze.Game.World;

/// <summary>
/// The tiles of one chunk of 16 by 16 tiles that a sound blocking object covers, one bit per tile. Immutable.
/// </summary>
internal readonly struct SoundChunk : IEquatable<SoundChunk>
{
    /// <summary>
    /// The width and depth of a chunk in tiles.
    /// </summary>
    public const int Size = 16;

    // Four rows of 16 bits each, row 0 in the lowest bits of _rows0; bit x of a row is the tile at x.
    private readonly ulong _rows0;
    private readonly ulong _rows1;
    private readonly ulong _rows2;
    private readonly ulong _rows3;

    private SoundChunk(ulong rows0, ulong rows1, ulong rows2, ulong rows3)
    {
        _rows0 = rows0;
        _rows1 = rows1;
        _rows2 = rows2;
        _rows3 = rows3;
    }

    /// <summary>
    /// Whether no tile of the chunk is blocked.
    /// </summary>
    public bool IsEmpty => (_rows0 | _rows1 | _rows2 | _rows3) == 0;

    /// <summary>
    /// Creates a chunk from 16 rows, whose bit x is the tile at x of that row.
    /// </summary>
    /// <exception cref="ArgumentException">The span does not hold 16 rows.</exception>
    public static SoundChunk Create(ReadOnlySpan<ushort> rows)
    {
        if (rows.Length != Size)
        {
            throw new ArgumentException("A chunk has 16 rows.", nameof(rows));
        }

        return new SoundChunk(Pack(rows[..4]), Pack(rows[4..8]), Pack(rows[8..12]), Pack(rows[12..]));
    }

    /// <summary>
    /// Sets the bits of the area's tiles in the 16 rows of the chunk whose south-west tile is at the origin.
    /// </summary>
    public static void AddArea(Span<ushort> rows, long originX, long originY, GameArea area)
    {
        var firstX = Math.Max(area.X, originX);
        var lastX = Math.Min(area.Right, originX + Size - 1);
        var firstY = Math.Max(area.Y, originY);
        var lastY = Math.Min(area.Top, originY + Size - 1);
        if (firstX > lastX || firstY > lastY)
        {
            return;
        }

        var width = (int)(lastX - firstX + 1);
        var mask = (ushort)(((1 << width) - 1) << (int)(firstX - originX));
        for (var y = firstY; y <= lastY; y++)
        {
            rows[(int)(y - originY)] |= mask;
        }
    }

    /// <summary>
    /// Gets the row of the chunk, whose bit x is the tile at x.
    /// </summary>
    public int GetRow(int y)
    {
        var rows = (y >> 2) switch
        {
            0 => _rows0,
            1 => _rows1,
            2 => _rows2,
            _ => _rows3
        };

        return (int)(rows >> ((y & 3) * Size)) & 0xFFFF;
    }

    /// <summary>
    /// Whether the tile at the position inside the chunk, each from 0 to 15, is blocked.
    /// </summary>
    public bool IsBlocked(int x, int y) => ((GetRow(y) >> x) & 1) != 0;

    public bool Equals(SoundChunk other) =>
        _rows0 == other._rows0 && _rows1 == other._rows1 && _rows2 == other._rows2 && _rows3 == other._rows3;

    public override bool Equals(object? obj) => obj is SoundChunk other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_rows0, _rows1, _rows2, _rows3);

    public static bool operator ==(SoundChunk left, SoundChunk right) => left.Equals(right);

    public static bool operator !=(SoundChunk left, SoundChunk right) => !left.Equals(right);

    private static ulong Pack(ReadOnlySpan<ushort> rows) =>
        rows[0] | ((ulong)rows[1] << Size) | ((ulong)rows[2] << (2 * Size)) | ((ulong)rows[3] << (3 * Size));
}
