using HomeBlaze.Game.Abstractions;

namespace HomeBlaze.Game.World;

/// <summary>
/// The tiles of one chunk of the <see cref="GameWorldIndex"/> that a sound blocking object covers, one bit per tile.
/// Immutable.
/// </summary>
internal readonly struct SoundChunk : IEquatable<SoundChunk>
{
    private const int Size = GameWorldIndex.ChunkSize;
    private const int RowsPerWord = 64 / Size;

    // The 256 tiles of a chunk of 16 by 16, which this layout requires: four rows of 16 bits in each word, row 0 in the
    // lowest bits of _rows0; bit x of a row is the tile at x.
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
    /// Creates a chunk from its rows, one per tile of its depth, whose bit x is the tile at x of that row.
    /// </summary>
    /// <exception cref="ArgumentException">The span does not hold one row per tile of the depth.</exception>
    public static SoundChunk Create(ReadOnlySpan<ushort> rows)
    {
        if (rows.Length != Size)
        {
            throw new ArgumentException("A chunk has one row per tile of its depth.", nameof(rows));
        }

        return new SoundChunk(
            Pack(rows[..RowsPerWord]), Pack(rows[RowsPerWord..(2 * RowsPerWord)]),
            Pack(rows[(2 * RowsPerWord)..(3 * RowsPerWord)]), Pack(rows[(3 * RowsPerWord)..]));
    }

    /// <summary>
    /// Sets the bits of the area's tiles in the rows of the chunk whose south-west tile is at the origin.
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
        var rows = (y / RowsPerWord) switch
        {
            0 => _rows0,
            1 => _rows1,
            2 => _rows2,
            _ => _rows3
        };

        return (int)(rows >> (y % RowsPerWord * Size)) & ((1 << Size) - 1);
    }

    /// <summary>
    /// Whether the tile at the position inside the chunk, each from 0 to <see cref="GameWorldIndex.ChunkMask"/>, is blocked.
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
