using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// Reads single tiles of an index's sound blocking, for tests of the index.
/// </summary>
internal static class SoundIndexExtensions
{
    /// <summary>
    /// Whether a sound blocking entity covers the tile, whether visible or not.
    /// </summary>
    public static bool IsSoundBlocked(this GameWorldIndex index, long x, long y) =>
        index.GetSoundChunk(GameWorldIndex.GetChunk(x, y))
            .IsBlocked((int)(x & GameWorldIndex.ChunkMask), (int)(y & GameWorldIndex.ChunkMask));
}
