using HomeBlaze.Game.Abstractions;

namespace HomeBlaze.Game.World;

/// <summary>
/// Finds the length of the shortest open path that sound takes from a source footprint to the footprints around it.
/// Paths run between tile centers. A tile is open while no object with <see cref="GameObject.IsSoundBlocking"/> covers
/// it; the tiles of the source footprint always are. Steps go to the 16 tiles at the offsets (1, 0), (1, 1) and (2, 1)
/// in every orientation, with their lengths in <see cref="Unit"/> rounded to integers, and need every tile that the
/// segment between the two centers passes or touches to be open: a diagonal step also both tiles beside its corner, so
/// it never cuts through a joint where two walls meet at a corner, and a step to (2, 1) also the tiles (1, 0) and
/// (1, 1). Keeps the lengths of recent small sources while the <see cref="SoundMap.Version"/> stays the same. Not
/// thread-safe: reuses its buffers from search to search.
/// </summary>
/// <remarks>
/// A target tile that is not open still gets the length of a step into it, so a target footprint that overlaps a
/// sound blocking object is reached through its open neighbors; such a tile never continues a path. A grid length
/// exceeds the straight length by at most 2.75 %; <see cref="GetOpenLength"/> gives the grid length without walls to
/// compare with.
/// </remarks>
internal sealed class SoundPathSearch
{
    /// <summary>
    /// The length of one tile in the integer lengths of this search.
    /// </summary>
    public const int Unit = 1000;

    /// <summary>
    /// The length of a target that no open path within the hearing radius plus 2 tiles reaches.
    /// </summary>
    public const int NoPath = int.MaxValue;

    private const int StraightLength = Unit;
    private const int DiagonalLength = 1414;
    private const int KnightLength = 2236;

    // Beyond the hearing radius, because a grid length exceeds the straight length by up to 2.75 %, 1.1 tiles at the
    // radius, and the hearing range subtracts that excess again.
    private const int Margin = HearingRange.HearingRadius + 2;
    private const int MaximumLength = Margin * Unit;

    // The length of a tile no path reaches, in the buffers.
    private const ushort Unreached = ushort.MaxValue;

    // Closed tiles around the grid, so the longest step never leaves the buffers.
    private const int Padding = 2;

    // Sources up to this width and depth, such as players, keep their lengths, in buffers of the size of their grid; at
    // about 17 KB each the cache holds at most 4.3 MB.
    private const int MaximumCachedSize = 4;
    private const int CachedTileCount = (MaximumCachedSize + 2 * Margin + 2 * Padding) * (MaximumCachedSize + 2 * Margin + 2 * Padding);
    private const int CacheCapacity = 256;

    // Each step with the two tiles besides its target that it passes or touches; a straight step names its own tile.
    private static readonly Step[] Steps =
    [
        new(1, 0, StraightLength, 0, 0, 0, 0), new(-1, 0, StraightLength, 0, 0, 0, 0),
        new(0, 1, StraightLength, 0, 0, 0, 0), new(0, -1, StraightLength, 0, 0, 0, 0),
        new(1, 1, DiagonalLength, 1, 0, 0, 1), new(1, -1, DiagonalLength, 1, 0, 0, -1),
        new(-1, 1, DiagonalLength, -1, 0, 0, 1), new(-1, -1, DiagonalLength, -1, 0, 0, -1),
        new(2, 1, KnightLength, 1, 0, 1, 1), new(2, -1, KnightLength, 1, 0, 1, -1),
        new(-2, 1, KnightLength, -1, 0, -1, 1), new(-2, -1, KnightLength, -1, 0, -1, -1),
        new(1, 2, KnightLength, 0, 1, 1, 1), new(1, -2, KnightLength, 0, -1, 1, -1),
        new(-1, 2, KnightLength, 0, 1, -1, 1), new(-1, -2, KnightLength, 0, -1, -1, -1),
    ];

    private readonly int[] _stepOffsets = new int[Steps.Length];
    private readonly int[] _firstPassedOffsets = new int[Steps.Length];
    private readonly int[] _secondPassedOffsets = new int[Steps.Length];
    // The tiles to expand by length, in buckets as wide as the shortest step: a step from a tile of one bucket always
    // lands in a later one, so the lengths of a bucket are final once it is reached and its tiles expand in any order.
    private readonly List<int>[] _buckets = CreateBuckets();
    private int[] _expandedStamps = [];
    private int _expandStamp;
    private readonly Dictionary<GameArea, CacheEntry> _cache = new();
    private readonly Stack<ushort[]> _pooledLengths = new();
    private readonly long[] _lastUses = new long[CacheCapacity];
    private bool[] _openTiles = [];
    private ushort[] _uncachedLengths = [];
    private ushort[] _lengths = [];
    private long _useCount;

    // The tile at index 0 and the size of the grid, padding included; a row holds _columns tiles.
    private long _originX;
    private long _originY;
    private int _columns;
    private int _rows;

    // The source of the lengths in _lengths, and the map and version that _lengths and the cache hold lengths for; no
    // lengths for a source of an invalid size.
    private bool _isPrepared;
    private bool _hasLengths;
    private GameArea _source;
    private SoundMap? _map;
    private long _mapVersion;

    /// <summary>
    /// The number of searches that ran, for tests of the cache.
    /// </summary>
    internal long SearchCount { get; private set; }

    /// <summary>
    /// Computes the lengths from the source footprint, unless they are already known for this footprint and the current
    /// <see cref="SoundMap.Version"/> of this map.
    /// </summary>
    public void Prepare(GameArea source, SoundMap map)
    {
        if (!ReferenceEquals(_map, map) || _mapVersion != map.Version)
        {
            ClearCache();
            _map = map;
            _mapVersion = map.Version;
        }
        else if (_isPrepared && _source == source)
        {
            return;
        }

        _isPrepared = false;
        _hasLengths = HasValidSize(source);
        if (_hasLengths)
        {
            SetGrid(source);
            if (!IsCacheable(source))
            {
                _lengths = _uncachedLengths;
                Search(source, map);
            }
            else if (_cache.TryGetValue(source, out var entry))
            {
                entry.LastUse = ++_useCount;
                _lengths = entry.Lengths;
            }
            else
            {
                _lengths = RentCachedLengths();
                Search(source, map);
                _cache.Add(source, new CacheEntry(_lengths) { LastUse = ++_useCount });
            }
        }

        _source = source;
        _isPrepared = true;
    }

    /// <summary>
    /// Drops the cached lengths and the prepared source, as after a failure in the middle of a search.
    /// </summary>
    public void Reset()
    {
        ClearCache();
        _map = null;
        _isPrepared = false;
    }

    /// <summary>
    /// Gets the length of the shortest open path from the center of the prepared source to the center of the target,
    /// in <see cref="Unit"/>, or <see cref="NoPath"/>, also for a source or target of an invalid size.
    /// </summary>
    /// <exception cref="InvalidOperationException">No source is prepared.</exception>
    public int GetLength(GameArea target)
    {
        if (!_isPrepared)
        {
            throw new InvalidOperationException("No source is prepared.");
        }

        if (!_hasLengths || !HasValidSize(target))
        {
            return NoPath;
        }

        var shortest = (long)NoPath;
        var lastY = Math.Min(target.Top, _originY + _rows - 1 - Padding);
        for (var y = Math.Max(target.Y, _originY + Padding); y <= lastY; y++)
        {
            shortest = Math.Min(shortest, GetShortestInRow(target, y));
        }

        return shortest <= MaximumLength ? (int)shortest : NoPath;
    }

    /// <summary>
    /// Gets the length in <see cref="Unit"/> of a path between the centers of two footprints without walls, by the same
    /// rule as <see cref="GetLength"/>, through the tiles nearest to each center.
    /// </summary>
    public static long GetOpenLength(GameArea first, GameArea second)
    {
        var columns = GetCenterTiles(first.X, first.Width);
        var rows = GetCenterTiles(first.Y, first.Depth);
        var shortest = long.MaxValue;
        for (var y = rows.First; y <= rows.Last; y++)
        {
            for (var x = columns.First; x <= columns.Last; x++)
            {
                shortest = Math.Min(shortest, GetCenterLength(first, x, y) + GetOpenLengthTo(x, y, second));
            }
        }

        return shortest;
    }

    private static long GetOpenLengthTo(long x, long y, GameArea target)
    {
        var columns = GetCenterTiles(target.X, target.Width);
        var rows = GetCenterTiles(target.Y, target.Depth);
        var shortest = long.MaxValue;
        for (var targetY = rows.First; targetY <= rows.Last; targetY++)
        {
            for (var targetX = columns.First; targetX <= columns.Last; targetX++)
            {
                var length = GetOffsetLength(2 * (targetX - x), 2 * (targetY - y)) + GetCenterLength(target, targetX, targetY);
                shortest = Math.Min(shortest, length);
            }
        }

        return shortest;
    }

    // The one tile at the center of an odd size, or the two beside the center of an even one.
    private static (long First, long Last) GetCenterTiles(int start, int size) =>
        ((long)start + (size - 1) / 2, (long)start + size / 2);

    // The length from the center of the footprint to the center of the tile.
    private static long GetCenterLength(GameArea footprint, long x, long y) =>
        GetOffsetLength(
            2 * x + 1 - (2L * footprint.X + footprint.Width),
            2 * y + 1 - (2L * footprint.Y + footprint.Depth));

    // The grid length without walls of an offset in half tiles, through the two step directions next to its direction.
    private static long GetOffsetLength(long doubledDeltaX, long doubledDeltaY)
    {
        var longer = Math.Max(Math.Abs(doubledDeltaX), Math.Abs(doubledDeltaY));
        var shorter = Math.Min(Math.Abs(doubledDeltaX), Math.Abs(doubledDeltaY));
        var doubled = 2 * shorter <= longer
            ? shorter * KnightLength + (longer - 2 * shorter) * StraightLength
            : (2 * shorter - longer) * DiagonalLength + (longer - shorter) * KnightLength;
        return doubled / 2;
    }

    private static bool HasValidSize(GameArea footprint) =>
        GameWorldIndex.IsValidSize(footprint.Width) && GameWorldIndex.IsValidSize(footprint.Depth);

    private static bool IsCacheable(GameArea source) => source.Width <= MaximumCachedSize && source.Depth <= MaximumCachedSize;

    private void Search(GameArea source, SoundMap map)
    {
        SearchCount++;
        MarkOpenTiles(source, map);
        Seed(source);
        Expand();
    }

    private ushort[] RentCachedLengths()
    {
        if (_pooledLengths.Count == 0 && _cache.Count >= CacheCapacity)
        {
            EvictOlderHalf();
        }

        return _pooledLengths.Count > 0 ? _pooledLengths.Pop() : new ushort[CachedTileCount];
    }

    private void EvictOlderHalf()
    {
        var count = 0;
        foreach (var entry in _cache.Values)
        {
            _lastUses[count++] = entry.LastUse;
        }

        Array.Sort(_lastUses, 0, count);
        var median = _lastUses[count / 2];
        foreach (var (source, entry) in _cache)
        {
            if (entry.LastUse < median)
            {
                _cache.Remove(source);
                _pooledLengths.Push(entry.Lengths);
            }
        }
    }

    private void ClearCache()
    {
        foreach (var entry in _cache.Values)
        {
            _pooledLengths.Push(entry.Lengths);
        }

        _cache.Clear();
        _isPrepared = false;
    }

    private long GetShortestInRow(GameArea target, long y)
    {
        var shortest = (long)NoPath;
        var lastX = Math.Min(target.Right, _originX + _columns - 1 - Padding);
        for (var x = Math.Max(target.X, _originX + Padding); x <= lastX; x++)
        {
            var length = _lengths[GetTile(x, y)];
            if (length != Unreached)
            {
                shortest = Math.Min(shortest, length + GetCenterLength(target, x, y));
            }
        }

        return shortest;
    }

    private int GetTile(long x, long y) => (int)((y - _originY) * _columns + (x - _originX));

    private void SetGrid(GameArea source)
    {
        var firstX = Math.Max(int.MinValue, source.X - (long)Margin);
        var lastX = Math.Min(int.MaxValue, source.Right + Margin);
        var firstY = Math.Max(int.MinValue, source.Y - (long)Margin);
        var lastY = Math.Min(int.MaxValue, source.Top + Margin);
        _originX = firstX - Padding;
        _originY = firstY - Padding;
        _columns = (int)(lastX - firstX + 1) + 2 * Padding;
        _rows = (int)(lastY - firstY + 1) + 2 * Padding;
        for (var step = 0; step < Steps.Length; step++)
        {
            var (x, y, _, firstPassedX, firstPassedY, secondPassedX, secondPassedY) = Steps[step];
            _stepOffsets[step] = y * _columns + x;
            _firstPassedOffsets[step] = firstPassedY * _columns + firstPassedX;
            _secondPassedOffsets[step] = secondPassedY * _columns + secondPassedX;
        }

        var tileCount = _columns * _rows;
        if (_openTiles.Length < tileCount)
        {
            _openTiles = new bool[tileCount];
            _expandedStamps = new int[tileCount];
        }

        if (!IsCacheable(source) && _uncachedLengths.Length < tileCount)
        {
            _uncachedLengths = new ushort[tileCount];
        }
    }

    // Opens the tiles inside the padding that no sound blocking object covers and opens the source footprint again.
    private void MarkOpenTiles(GameArea source, SoundMap map)
    {
        Array.Clear(_openTiles, 0, _columns * _rows);
        var area = new GameArea(
            (int)(_originX + Padding), (int)(_originY + Padding), _columns - 2 * Padding, _rows - 2 * Padding);
        var lastChunkX = area.Right >> 4;
        for (var y = (long)area.Y; y <= area.Top; y++)
        {
            var chunkY = y >> 4;
            var row = (int)(y & 15);
            for (var chunkX = (long)area.X >> 4; chunkX <= lastChunkX; chunkX++)
            {
                var blocked = map.GetChunk((chunkX, chunkY)).GetRow(row);
                var lastX = Math.Min(area.Right, (chunkX << 4) + 15);
                var tile = GetTile(Math.Max(area.X, chunkX << 4), y);
                for (var x = Math.Max(area.X, chunkX << 4); x <= lastX; x++, tile++)
                {
                    _openTiles[tile] = ((blocked >> (int)(x & 15)) & 1) == 0;
                }
            }
        }

        MarkTiles(source, area, isOpen: true);
    }

    private void MarkTiles(GameArea footprint, GameArea area, bool isOpen)
    {
        var firstX = Math.Max(footprint.X, area.X);
        var lastX = Math.Min(footprint.Right, area.Right);
        var lastY = Math.Min(footprint.Top, area.Top);
        for (var y = (long)Math.Max(footprint.Y, area.Y); y <= lastY; y++)
        {
            Array.Fill(_openTiles, isOpen, GetTile(firstX, y), (int)(lastX - firstX + 1));
        }
    }

    // Starts at every tile of the footprint with its length from the center. A length beyond the maximum, only in a
    // large footprint, is stored as one more than the maximum: it neither continues a path nor reaches a target either way.
    private void Seed(GameArea source)
    {
        Array.Fill(_lengths, Unreached, 0, _columns * _rows);
        foreach (var bucket in _buckets)
        {
            bucket.Clear();
        }
        for (var y = (long)source.Y; y <= source.Top; y++)
        {
            for (var x = (long)source.X; x <= source.Right; x++)
            {
                var tile = GetTile(x, y);
                var length = (int)Math.Min(GetCenterLength(source, x, y), MaximumLength + 1);
                _lengths[tile] = (ushort)length;
                _buckets[length / StraightLength].Add(tile);
            }
        }
    }

    private void Expand()
    {
        if (++_expandStamp == int.MaxValue)
        {
            Array.Clear(_expandedStamps);
            _expandStamp = 1;
        }

        for (var index = 0; index < _buckets.Length; index++)
        {
            // A tile is queued again whenever its length shrinks; only an entry in the bucket of its final length expands
            // it, once.
            var bucket = _buckets[index];
            foreach (var tile in bucket)
            {
                int length = _lengths[tile];
                if (length / StraightLength == index && _expandedStamps[tile] != _expandStamp)
                {
                    _expandedStamps[tile] = _expandStamp;
                    ExpandTile(tile, length);
                }
            }

            bucket.Clear();
        }
    }

    // Lengths reach from 0 to one more than the maximum, which a seed of a large source may have.
    private static List<int>[] CreateBuckets()
    {
        var buckets = new List<int>[(MaximumLength + 1) / StraightLength + 1];
        for (var index = 0; index < buckets.Length; index++)
        {
            buckets[index] = [];
        }

        return buckets;
    }

    private void ExpandTile(int tile, int length)
    {
        for (var step = 0; step < Steps.Length; step++)
        {
            var next = tile + _stepOffsets[step];
            var nextLength = length + Steps[step].Length;
            if (nextLength <= MaximumLength && nextLength < _lengths[next] && IsPassable(tile, step))
            {
                _lengths[next] = (ushort)nextLength;
                if (_openTiles[next])
                {
                    _buckets[nextLength / StraightLength].Add(next);
                }
            }
        }
    }

    private bool IsPassable(int tile, int step) =>
        _openTiles[tile + _firstPassedOffsets[step]] && _openTiles[tile + _secondPassedOffsets[step]];

    private sealed class CacheEntry(ushort[] lengths)
    {
        public ushort[] Lengths { get; } = lengths;

        public long LastUse { get; set; }
    }

    private readonly record struct Step(
        int X, int Y, int Length, int FirstPassedX, int FirstPassedY, int SecondPassedX, int SecondPassedY);
}
