using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.Simulation;

namespace HomeBlaze.Game.World;

/// <summary>
/// Reads the sound blocked tiles of the current <see cref="SoundSnapshot"/> and labels the regions of connected open
/// tiles, caching both until a snapshot of another version comes. Not thread-safe: the volume table build owns it, and
/// the snapshots it gets must come from one index.
/// </summary>
internal sealed class SoundMap
{
    /// <summary>
    /// The region of a footprint with a sound blocked tile, of an invalid size or too large to label, or found when no
    /// label was left.
    /// </summary>
    public const int UnknownRegion = 0;

    /// <summary>
    /// The region of open tiles too large to count as closed, more than <see cref="MaximumClosedTiles"/> tiles.
    /// </summary>
    public const int OpenRegion = 1;

    /// <summary>
    /// The most tiles of a closed region, about the search grid of <see cref="SoundPathSearch"/> around a player; a
    /// larger region counts as open, so its pairs are searched.
    /// </summary>
    public const int MaximumClosedTiles = (2 * (HearingRange.HearingRadius + 2) + 2) * (2 * (HearingRange.HearingRadius + 2) + 2);

    private const int ChunkShift = GameWorldIndex.ChunkShift;
    private const int ChunkMask = GameWorldIndex.ChunkMask;
    private const int MaximumFootprintTiles = 256;

    // The caches are dropped with each new version, and otherwise only on the next Update once this large, so a world
    // explored widely in one version keeps them within about 4 MB of chunks and 8 MB of labels (512 bytes per chunk).
    private const int MaximumCachedChunks = 65536;
    private const int MaximumLabeledChunks = 16384;
    private const int MaximumPooledLabels = 1024;
    private const int TileCount = GameWorldIndex.ChunkSize * GameWorldIndex.ChunkSize;

    private readonly Dictionary<(long X, long Y), SoundChunk> _chunks = new();
    private readonly Dictionary<(long X, long Y), ushort[]> _labels = new();
    private readonly Stack<ushort[]> _pooledLabels = new();
    private readonly Queue<(long X, long Y)> _fillQueue = new();
    private readonly List<(long X, long Y)> _filledTiles = [];
    private SoundSnapshot _snapshot = SoundSnapshot.Empty;
    private int _nextRegion = OpenRegion + 1;

    // The chunk that the last lookup found, which a walk along a line or a row mostly asks for again.
    private (long X, long Y) _lastChunkKey = (long.MinValue, long.MinValue);
    private SoundChunk _lastChunk;

    /// <summary>
    /// The <see cref="SoundSnapshot.Version"/> of the current snapshot; the caches of a reader stay valid while it does
    /// not change.
    /// </summary>
    public long Version => _snapshot.Version;

    /// <summary>
    /// Reads the snapshot from now on; drops the cached tiles and labels when its version differs from the current one.
    /// Call it only between two uses, never during one, since labels taken before and after it may not match.
    /// </summary>
    public void Update(SoundSnapshot snapshot)
    {
        if (snapshot.Version != _snapshot.Version || _chunks.Count > MaximumCachedChunks)
        {
            _chunks.Clear();
            _lastChunkKey = (long.MinValue, long.MinValue);
        }

        // Labels run out only after tens of thousands of closed regions; clearing them early keeps a build from doing so.
        if (snapshot.Version != _snapshot.Version || _labels.Count > MaximumLabeledChunks || _nextRegion > ushort.MaxValue / 2)
        {
            ClearLabels();
        }

        _snapshot = snapshot;
    }

    /// <summary>
    /// Drops the cached tiles and labels and reads the empty snapshot, as after a failure in the middle of a use.
    /// </summary>
    public void Reset()
    {
        _chunks.Clear();
        _lastChunkKey = (long.MinValue, long.MinValue);
        _fillQueue.Clear();
        _filledTiles.Clear();
        ClearLabels();
        _snapshot = SoundSnapshot.Empty;
    }

    /// <summary>
    /// Whether a sound blocking object covers the tile.
    /// </summary>
    public bool IsSoundBlocked(long x, long y) =>
        GetChunk((x >> ChunkShift, y >> ChunkShift)).IsBlocked((int)(x & ChunkMask), (int)(y & ChunkMask));

    /// <summary>
    /// Gets the sound blocked tiles of the chunk.
    /// </summary>
    public SoundChunk GetChunk((long X, long Y) chunk)
    {
        if (chunk == _lastChunkKey)
        {
            return _lastChunk;
        }

        if (!_chunks.TryGetValue(chunk, out var tiles))
        {
            tiles = _snapshot.GetChunk(chunk);
            _chunks.Add(chunk, tiles);
        }

        _lastChunkKey = chunk;
        _lastChunk = tiles;
        return tiles;
    }

    /// <summary>
    /// Gets the region that the open tiles of the footprint lie in: a label from 2 up shared by all tiles of one closed
    /// region, <see cref="OpenRegion"/>, or <see cref="UnknownRegion"/> when a tile of the footprint is sound blocked.
    /// </summary>
    /// <remarks>
    /// Regions are connected by the steps of <see cref="SoundPathSearch"/>, between open tiles whose passed tiles are open.
    /// These connect exactly the tiles that steps to the 4 direct neighbors connect, because each longer step passes
    /// open tiles that such steps join; so the labels come from a flood fill over the 4 neighbors.
    /// </remarks>
    public int GetRegion(GameArea footprint)
    {
        if (!IsLabeledSize(footprint))
        {
            return UnknownRegion;
        }

        for (var y = (long)footprint.Y; y <= footprint.Top; y++)
        {
            for (var x = (long)footprint.X; x <= footprint.Right; x++)
            {
                if (IsSoundBlocked(x, y))
                {
                    return UnknownRegion;
                }
            }
        }

        // The open tiles of a footprint are direct neighbors, so they all lie in one region.
        var label = GetLabel(footprint.X, footprint.Y);
        return label != UnknownRegion ? label : Fill(footprint.X, footprint.Y);
    }

    /// <summary>
    /// Whether no open path joins footprints in the regions from <see cref="GetRegion"/>: they lie in two different
    /// known regions, of which at least one is therefore closed.
    /// </summary>
    public static bool AreSeparated(int firstRegion, int secondRegion) =>
        firstRegion != secondRegion && IsKnown(firstRegion) && IsKnown(secondRegion);

    private static bool IsKnown(int region) => region != UnknownRegion;

    private static bool IsLabeledSize(GameArea footprint) =>
        GameWorldIndex.IsValidSize(footprint.Width) && GameWorldIndex.IsValidSize(footprint.Depth)
        && footprint.Width * footprint.Depth <= MaximumFootprintTiles;

    // Labels the open tiles connected to the open tile with a new label, or all of them open once they exceed the
    // maximum or reach a tile labeled open; labels nothing once no label is left, which only costs searches.
    private int Fill(long x, long y)
    {
        if (_nextRegion > ushort.MaxValue)
        {
            return UnknownRegion;
        }

        var label = _nextRegion++;
        var isOpen = false;
        _filledTiles.Clear();
        _fillQueue.Clear();
        Visit(x, y, label);
        while (_fillQueue.TryDequeue(out var tile))
        {
            if (!TryVisitNeighbors(tile.X, tile.Y, label) || _filledTiles.Count > MaximumClosedTiles)
            {
                isOpen = true;
                break;
            }
        }

        if (isOpen)
        {
            foreach (var (filledX, filledY) in _filledTiles)
            {
                SetLabel(filledX, filledY, OpenRegion);
            }

            label = OpenRegion;
        }

        _fillQueue.Clear();
        _filledTiles.Clear();
        return label;
    }

    private bool TryVisitNeighbors(long x, long y, int label) =>
        TryVisitNeighbor(x + 1, y, label) && TryVisitNeighbor(x - 1, y, label)
        && TryVisitNeighbor(x, y + 1, label) && TryVisitNeighbor(x, y - 1, label);

    // Returns false when the neighbor is labeled open, which makes the whole fill open.
    private bool TryVisitNeighbor(long x, long y, int label)
    {
        if (IsSoundBlocked(x, y))
        {
            return true;
        }

        var existing = GetLabel(x, y);
        if (existing == OpenRegion)
        {
            return false;
        }

        if (existing == UnknownRegion)
        {
            Visit(x, y, label);
        }

        return true;
    }

    private void Visit(long x, long y, int label)
    {
        SetLabel(x, y, label);
        _filledTiles.Add((x, y));
        _fillQueue.Enqueue((x, y));
    }

    private int GetLabel(long x, long y) =>
        _labels.TryGetValue((x >> ChunkShift, y >> ChunkShift), out var labels) ? labels[GetLabelIndex(x, y)] : UnknownRegion;

    private void SetLabel(long x, long y, int label)
    {
        var chunk = (x >> ChunkShift, y >> ChunkShift);
        if (!_labels.TryGetValue(chunk, out var labels))
        {
            labels = _pooledLabels.Count > 0 ? _pooledLabels.Pop() : new ushort[TileCount];
            _labels.Add(chunk, labels);
        }

        labels[GetLabelIndex(x, y)] = (ushort)label;
    }

    private static int GetLabelIndex(long x, long y) => (int)(((y & ChunkMask) << ChunkShift) | (x & ChunkMask));

    private void ClearLabels()
    {
        foreach (var labels in _labels.Values)
        {
            if (_pooledLabels.Count < MaximumPooledLabels)
            {
                Array.Clear(labels);
                _pooledLabels.Push(labels);
            }
        }

        _labels.Clear();
        _nextRegion = OpenRegion + 1;
    }
}
