namespace HomeBlaze.Game.World;

/// <summary>
/// Finds the length of the shortest open path that sound takes from a source footprint to the footprints around it.
/// Paths run over the corners of the tiles, called vertices here. A vertex is open while none of its four tiles blocks
/// sound by <see cref="GameWorldIndex.IsSoundBlocked"/>. A path therefore never touches a blocking tile, not even at a
/// corner, and never slips through a joint where two walls meet at a corner. Steps go to the 16 vertices at the offsets
/// (1, 0), (1, 1) and (2, 1) in every orientation, with their lengths in <see cref="Unit"/> rounded to integers. Every
/// tile that such a step touches belongs to one of its two vertices, so a step between open vertices needs no further
/// check. Not thread-safe: reuses its buffers from search to search.
/// </summary>
/// <remarks>
/// The lengths are exact integer sums on an undirected graph, and a path starts and ends the same way at either
/// footprint, so both directions give the same length. A grid length exceeds the straight length by at most 2.75 %;
/// <see cref="GetOpenLength"/> gives the grid length without walls to compare with.
/// </remarks>
internal sealed class SoundPathSearch
{
    /// <summary>
    /// The length of one tile in the integer lengths of this search.
    /// </summary>
    public const int Unit = 1000;

    /// <summary>
    /// The length of a target that no open path within <see cref="MaximumLength"/> reaches.
    /// </summary>
    public const int NoPath = int.MaxValue;

    private const int StraightLength = Unit;
    private const int DiagonalLength = 1414;
    private const int KnightLength = 2236;

    // Beyond the hearing radius, because a grid length exceeds the straight length by up to 2.75 %, 1.1 tiles at the
    // radius, and the hearing range subtracts that excess again.
    private const int Margin = HearingRange.HearingRadius + 2;
    private const int MaximumLength = Margin * Unit;

    // Closed vertices around the grid, so the longest step never leaves the buffers.
    private const int Padding = 2;

    private static readonly (int X, int Y, int Length)[] Steps =
    [
        (1, 0, StraightLength), (-1, 0, StraightLength), (0, 1, StraightLength), (0, -1, StraightLength),
        (1, 1, DiagonalLength), (1, -1, DiagonalLength), (-1, 1, DiagonalLength), (-1, -1, DiagonalLength),
        (2, 1, KnightLength), (2, -1, KnightLength), (-2, 1, KnightLength), (-2, -1, KnightLength),
        (1, 2, KnightLength), (1, -2, KnightLength), (-1, 2, KnightLength), (-1, -2, KnightLength),
    ];

    private readonly int[] _stepOffsets = new int[Steps.Length];
    private readonly PriorityQueue<int, int> _queue = new();
    private readonly List<Footprint> _blockers = [];
    private bool[] _blockedTiles = [];
    private bool[] _openVertices = [];
    private int[] _lengths = [];

    // The vertex at index 0 and the size of the grid, padding included; a row holds _columns vertices.
    private long _originX;
    private long _originY;
    private int _columns;
    private int _rows;

    // The source and index state of the lengths in _lengths.
    private bool _hasLengths;
    private Footprint _source;
    private GameWorldIndex? _index;
    private long _indexVersion;

    /// <summary>
    /// Computes the lengths from the source footprint, unless they are already computed for this footprint and the
    /// current <see cref="GameWorldIndex.Version"/> of this index.
    /// </summary>
    public void Prepare(Footprint source, GameWorldIndex index)
    {
        if (_hasLengths && _source == source && ReferenceEquals(_index, index) && _indexVersion == index.Version)
        {
            return;
        }

        _hasLengths = false;
        SetGrid(source);
        MarkBlockedTiles(index);
        MarkOpenVertices();
        Seed(source);
        Expand();
        _source = source;
        _index = index;
        _indexVersion = index.Version;
        _hasLengths = true;
    }

    /// <summary>
    /// Gets the length of the shortest open path from the center of the prepared source to the center of the target,
    /// in <see cref="Unit"/>, or <see cref="NoPath"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">No source is prepared.</exception>
    public int GetLength(Footprint target)
    {
        if (!_hasLengths)
        {
            throw new InvalidOperationException("No source is prepared.");
        }

        var center = GetDoubledCenter(target);
        var shortest = (long)NoPath;
        var lastY = Math.Min(target.Top + 1, _originY + _rows - 1 - Padding);
        for (var y = Math.Max(target.Y, _originY + Padding); y <= lastY; y++)
        {
            shortest = Math.Min(shortest, GetShortestInRow(target, y, center));
        }

        return shortest <= MaximumLength ? (int)shortest : NoPath;
    }

    /// <summary>
    /// Gets the length in <see cref="Unit"/> of a path between two points without walls, from their offset in half tiles.
    /// </summary>
    public static long GetOpenLength(long doubledDeltaX, long doubledDeltaY)
    {
        var longer = Math.Max(Math.Abs(doubledDeltaX), Math.Abs(doubledDeltaY));
        var shorter = Math.Min(Math.Abs(doubledDeltaX), Math.Abs(doubledDeltaY));

        // The two step directions next to the direction of the offset, in half tiles.
        var doubled = 2 * shorter <= longer
            ? shorter * KnightLength + (longer - 2 * shorter) * StraightLength
            : (2 * shorter - longer) * DiagonalLength + (longer - shorter) * KnightLength;
        return doubled / 2;
    }

    // In half tiles, so the center of every footprint is a whole number.
    private static (long X, long Y) GetDoubledCenter(Footprint footprint) =>
        (2L * footprint.X + footprint.Width, 2L * footprint.Y + footprint.Depth);

    private long GetShortestInRow(Footprint target, long y, (long X, long Y) center)
    {
        var shortest = (long)NoPath;
        var lastX = Math.Min(target.Right + 1, _originX + _columns - 1 - Padding);
        for (var x = Math.Max(target.X, _originX + Padding); x <= lastX; x++)
        {
            var length = _lengths[GetVertex(x, y)];
            if (length != NoPath)
            {
                shortest = Math.Min(shortest, length + GetOpenLength(2 * x - center.X, 2 * y - center.Y));
            }
        }

        return shortest;
    }

    private int GetVertex(long x, long y) => (int)((y - _originY) * _columns + (x - _originX));

    // Vertices exist from the int minimum to one past the int maximum, the corners of every tile.
    private void SetGrid(Footprint source)
    {
        var firstX = Math.Max(int.MinValue, source.X - (long)Margin);
        var lastX = Math.Min(int.MaxValue + 1L, source.Right + 1 + Margin);
        var firstY = Math.Max(int.MinValue, source.Y - (long)Margin);
        var lastY = Math.Min(int.MaxValue + 1L, source.Top + 1 + Margin);
        _originX = firstX - Padding;
        _originY = firstY - Padding;
        _columns = (int)(lastX - firstX + 1) + 2 * Padding;
        _rows = (int)(lastY - firstY + 1) + 2 * Padding;
        for (var step = 0; step < Steps.Length; step++)
        {
            _stepOffsets[step] = Steps[step].Y * _columns + Steps[step].X;
        }

        // The tile grid has one column and row more: tile (x, y) has the vertex (x, y) as its south-west corner.
        var vertexCount = _columns * _rows;
        var tileCount = (_columns + 1) * (_rows + 1);
        if (_lengths.Length < vertexCount)
        {
            _lengths = new int[vertexCount];
            _openVertices = new bool[vertexCount];
        }

        if (_blockedTiles.Length < tileCount)
        {
            _blockedTiles = new bool[tileCount];
        }
    }

    // The tile at column 0 is the one west of the vertex at column 0, so tile column = tile x - _originX + 1.
    private void MarkBlockedTiles(GameWorldIndex index)
    {
        var tileCount = (_columns + 1) * (_rows + 1);
        Array.Clear(_blockedTiles, 0, tileCount);
        var firstX = Math.Max(int.MinValue, _originX - 1);
        var lastX = Math.Min(int.MaxValue, _originX + _columns - 1);
        var firstY = Math.Max(int.MinValue, _originY - 1);
        var lastY = Math.Min(int.MaxValue, _originY + _rows - 1);
        var area = new Footprint((int)firstX, (int)firstY, (int)(lastX - firstX + 1), (int)(lastY - firstY + 1));
        _blockers.Clear();
        index.QuerySoundBlocking(area, _blockers);
        foreach (var blocker in _blockers)
        {
            MarkBlocker(blocker, area);
        }

        _blockers.Clear();
    }

    private void MarkBlocker(Footprint blocker, Footprint area)
    {
        var firstX = Math.Max(blocker.X, area.X) - _originX + 1;
        var lastX = Math.Min(blocker.Right, area.Right) - _originX + 1;
        var firstY = Math.Max(blocker.Y, area.Y) - _originY + 1;
        var lastY = Math.Min(blocker.Top, area.Top) - _originY + 1;
        var tileColumns = _columns + 1;
        for (var row = firstY; row <= lastY; row++)
        {
            var rowStart = (int)(row * tileColumns);
            Array.Fill(_blockedTiles, true, rowStart + (int)firstX, (int)(lastX - firstX + 1));
        }
    }

    // A vertex at column c and row r has the tiles at columns c and c + 1 and rows r and r + 1 around it.
    private void MarkOpenVertices()
    {
        var tileColumns = _columns + 1;
        Array.Clear(_openVertices, 0, _columns * _rows);
        for (var row = Padding; row < _rows - Padding; row++)
        {
            var south = row * tileColumns;
            var north = south + tileColumns;
            var vertexRow = row * _columns;
            for (var column = Padding; column < _columns - Padding; column++)
            {
                _openVertices[vertexRow + column] = !(_blockedTiles[south + column] || _blockedTiles[south + column + 1]
                    || _blockedTiles[north + column] || _blockedTiles[north + column + 1]);
            }
        }
    }

    // Starts at every open vertex of the footprint with its length from the center, which the footprint does not block.
    private void Seed(Footprint source)
    {
        Array.Fill(_lengths, NoPath, 0, _columns * _rows);
        _queue.Clear();
        var center = GetDoubledCenter(source);
        for (var y = (long)source.Y; y <= source.Top + 1; y++)
        {
            for (var x = (long)source.X; x <= source.Right + 1; x++)
            {
                SeedVertex(GetVertex(x, y), (int)GetOpenLength(2 * x - center.X, 2 * y - center.Y));
            }
        }
    }

    private void SeedVertex(int vertex, int length)
    {
        if (_openVertices[vertex] && length < _lengths[vertex])
        {
            _lengths[vertex] = length;
            _queue.Enqueue(vertex, length);
        }
    }

    private void Expand()
    {
        while (_queue.TryDequeue(out var vertex, out var length))
        {
            // A vertex is queued again whenever its length shrinks; only the entry with its final length expands it.
            if (length == _lengths[vertex])
            {
                ExpandVertex(vertex, length);
            }
        }
    }

    private void ExpandVertex(int vertex, int length)
    {
        for (var step = 0; step < Steps.Length; step++)
        {
            var next = vertex + _stepOffsets[step];
            var nextLength = length + Steps[step].Length;
            if (nextLength <= MaximumLength && nextLength < _lengths[next] && _openVertices[next])
            {
                _lengths[next] = nextLength;
                _queue.Enqueue(next, nextLength);
            }
        }
    }
}
