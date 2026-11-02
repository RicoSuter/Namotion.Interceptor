using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// Creates sound maps from an index, for tests of the parts that read sound blocking.
/// </summary>
internal static class SoundMapFactory
{
    /// <summary>
    /// Creates a map of the sound blocking entities the index holds; takes in the index's sound changes, so call it once
    /// per index.
    /// </summary>
    public static SoundMap Create(GameWorldIndex index)
    {
        var map = new SoundMap();
        map.Update(SoundSnapshot.Empty.TakeChanges(index));
        return map;
    }

    /// <summary>
    /// Gets the hearing position of the player.
    /// </summary>
    public static HearingPosition ToPosition(this PlayerEntity player) => new(player.ClientId, player.Area);
}
