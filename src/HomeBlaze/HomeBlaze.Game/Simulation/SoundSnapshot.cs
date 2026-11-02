using System.Collections.Immutable;
using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Simulation;

/// <summary>
/// An immutable copy of the sound blocked tiles per chunk, which any thread may read. The simulation builds the next
/// snapshot from the previous one by replacing only the changed chunks.
/// </summary>
internal sealed class SoundSnapshot
{
    public static readonly SoundSnapshot Empty = new(ImmutableDictionary<(long X, long Y), SoundChunk>.Empty, 0);

    private readonly ImmutableDictionary<(long X, long Y), SoundChunk> _chunks;

    private SoundSnapshot(ImmutableDictionary<(long X, long Y), SoundChunk> chunks, long version)
    {
        _chunks = chunks;
        Version = version;
    }

    /// <summary>
    /// The <see cref="GameWorldIndex.SoundVersion"/> of the index this snapshot was built from. Two snapshots of one
    /// index with the same version hold the same tiles.
    /// </summary>
    public long Version { get; }

    /// <summary>
    /// Gets the sound blocked tiles of the chunk.
    /// </summary>
    public SoundChunk GetChunk((long X, long Y) chunk) => _chunks.GetValueOrDefault(chunk);

    /// <summary>
    /// Returns a snapshot with the chunks of <see cref="GameWorldIndex.SoundChanges"/> copied from the index, and clears
    /// those changes; returns this snapshot when no tile differs. This snapshot stays unchanged.
    /// </summary>
    public SoundSnapshot TakeChanges(GameWorldIndex index)
    {
        var changes = index.SoundChanges;
        if (changes.Count == 0)
        {
            return this;
        }

        ImmutableDictionary<(long X, long Y), SoundChunk>.Builder? builder = null;
        foreach (var chunk in changes)
        {
            var tiles = index.GetSoundChunk(chunk);
            if (tiles == _chunks.GetValueOrDefault(chunk))
            {
                continue;
            }

            builder ??= _chunks.ToBuilder();
            if (tiles.IsEmpty)
            {
                builder.Remove(chunk);
            }
            else
            {
                builder[chunk] = tiles;
            }
        }

        changes.Clear();
        return builder is null ? this : new SoundSnapshot(builder.ToImmutable(), index.SoundVersion);
    }
}
