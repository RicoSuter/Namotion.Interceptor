using System.Collections.Immutable;
using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Simulation;

/// <summary>
/// An immutable copy of the movement blocking areas per chunk, which any thread may read. The simulation builds the next
/// snapshot from the previous one by replacing only the changed chunks.
/// </summary>
internal sealed class OccupancySnapshot
{
    public static readonly OccupancySnapshot Empty = new(ImmutableDictionary<(long X, long Y), ImmutableArray<(GameArea Area, GameObject Subject)>>.Empty);

    private readonly ImmutableDictionary<(long X, long Y), ImmutableArray<(GameArea Area, GameObject Subject)>> _chunks;

    private OccupancySnapshot(ImmutableDictionary<(long X, long Y), ImmutableArray<(GameArea Area, GameObject Subject)>> chunks)
    {
        _chunks = chunks;
    }

    /// <summary>
    /// Whether no movement blocking area other than those of <paramref name="except"/> overlaps the area. Visits every chunk
    /// the area covers, so the caller limits the area to sizes up to <see cref="GameObject.MaximumSize"/>.
    /// </summary>
    public bool IsFree(GameArea area, GameObject? except)
    {
        var (minimumX, minimumY) = GameWorldIndex.GetChunk(area.X, area.Y);
        var (maximumX, maximumY) = GameWorldIndex.GetChunk(area.Right, area.Top);
        for (var chunkY = minimumY; chunkY <= maximumY; chunkY++)
        {
            for (var chunkX = minimumX; chunkX <= maximumX; chunkX++)
            {
                if (_chunks.TryGetValue((chunkX, chunkY), out var entries) && IsOverlappedByAny(entries, area, except))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Returns a snapshot with the changed chunks copied from what the index stored; returns this snapshot when no
    /// movement blocking area of a changed chunk differs. This snapshot stays unchanged.
    /// </summary>
    public OccupancySnapshot With(GameWorldIndex index, ChangedChunks changes)
    {
        if (changes.Count == 0)
        {
            return this;
        }

        ImmutableDictionary<(long X, long Y), ImmutableArray<(GameArea Area, GameObject Subject)>>.Builder? builder = null;
        var entries = ImmutableArray.CreateBuilder<(GameArea Area, GameObject Subject)>();
        foreach (var chunk in changes.Chunks)
        {
            entries.Clear();
            index.QueryChunkMovementBlocking(chunk, entries);
            var hasExisting = _chunks.TryGetValue(chunk, out var existing);
            if (hasExisting ? AreEqual(existing, entries) : entries.Count == 0)
            {
                continue;
            }

            builder ??= _chunks.ToBuilder();
            if (entries.Count == 0)
            {
                builder.Remove(chunk);
            }
            else
            {
                builder[chunk] = entries.ToImmutable();
            }
        }

        return builder is null ? this : new OccupancySnapshot(builder.ToImmutable());
    }

    private static bool AreEqual(
        ImmutableArray<(GameArea Area, GameObject Subject)> existing, ImmutableArray<(GameArea Area, GameObject Subject)>.Builder entries)
    {
        if (existing.Length != entries.Count)
        {
            return false;
        }

        for (var i = 0; i < existing.Length; i++)
        {
            if (existing[i].Area != entries[i].Area || !ReferenceEquals(existing[i].Subject, entries[i].Subject))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsOverlappedByAny(ImmutableArray<(GameArea Area, GameObject Subject)> entries, GameArea area, GameObject? except)
    {
        foreach (var (blockingArea, subject) in entries)
        {
            if (!ReferenceEquals(subject, except) && blockingArea.Intersects(area))
            {
                return true;
            }
        }

        return false;
    }
}
