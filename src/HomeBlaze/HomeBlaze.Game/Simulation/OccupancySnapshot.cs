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
        var (minimumX, minimumY) = GameEntityIndex.GetChunk(area.X, area.Y);
        var (maximumX, maximumY) = GameEntityIndex.GetChunk(area.Right, area.Top);
        for (var chunkX = minimumX; chunkX <= maximumX; chunkX++)
        {
            for (var chunkY = minimumY; chunkY <= maximumY; chunkY++)
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
    /// Returns a snapshot with the changed chunks copied from the index, from the current area and movement blocking of
    /// their entities, which therefore must match what the index last took over; this one stays unchanged.
    /// <paramref name="scratch"/> is cleared and reused.
    /// </summary>
    public OccupancySnapshot With(GameEntityIndex index, ChangedChunks changes, List<GameEntity> scratch)
    {
        if (changes.Count == 0)
        {
            return this;
        }

        var builder = _chunks.ToBuilder();
        var entries = ImmutableArray.CreateBuilder<(GameArea Area, GameObject Subject)>();
        foreach (var chunk in changes.Chunks)
        {
            scratch.Clear();
            index.QueryChunk(chunk, scratch);
            entries.Clear();
            foreach (var entity in scratch)
            {
                if (entity.IsMovementBlocking)
                {
                    entries.Add((entity.Area, entity.Subject));
                }
            }

            if (entries.Count == 0)
            {
                builder.Remove(chunk);
            }
            else
            {
                builder[chunk] = entries.ToImmutable();
            }
        }

        scratch.Clear();
        return new OccupancySnapshot(builder.ToImmutable());
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
