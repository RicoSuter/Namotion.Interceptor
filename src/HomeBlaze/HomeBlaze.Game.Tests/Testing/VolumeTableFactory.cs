using HomeBlaze.Game.Voice;

namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// Creates volume tables from given rows, for tests of their readers.
/// </summary>
internal static class VolumeTableFactory
{
    /// <summary>
    /// Creates a table from the rows, copying them.
    /// </summary>
    public static GameVolumeTable Create(Dictionary<string, List<GameVolumeEntry>> rows)
    {
        var arrays = new Dictionary<string, GameVolumeEntry[]>(rows.Count);
        foreach (var (playerId, row) in rows)
        {
            arrays[playerId] = [.. row];
        }

        return GameVolumeTable.Wrap(arrays);
    }
}
