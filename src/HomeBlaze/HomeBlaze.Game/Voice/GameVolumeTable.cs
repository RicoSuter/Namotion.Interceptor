using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Voice;

/// <summary>
/// A player that a listener hears, and how loud, above 0 up to 1.
/// </summary>
internal readonly record struct GameVolumeEntry(string PlayerId, float Volume);

/// <summary>
/// For every connected player, the players it hears and their volume, by <see cref="HearingRange"/>. Never changed
/// after it is built, so any thread may read it.
/// </summary>
internal sealed class GameVolumeTable
{
    public static readonly GameVolumeTable Empty = new([]);

    private readonly Dictionary<string, GameVolumeEntry[]> _rows;

    private GameVolumeTable(Dictionary<string, GameVolumeEntry[]> rows)
    {
        _rows = rows;
    }

    /// <summary>
    /// Whether the player has a row, which every connected player has, also when it hears nobody.
    /// </summary>
    public bool HasRow(string playerId) => _rows.ContainsKey(playerId);

    /// <summary>
    /// Gets the players the player hears; empty for a player without a row.
    /// </summary>
    public ReadOnlySpan<GameVolumeEntry> GetRow(string playerId) =>
        _rows.TryGetValue(playerId, out var row) ? row : ReadOnlySpan<GameVolumeEntry>.Empty;

    /// <summary>
    /// Creates a table from the given rows, copying them.
    /// </summary>
    internal static GameVolumeTable Create(Dictionary<string, List<GameVolumeEntry>> rows)
    {
        var arrays = new Dictionary<string, GameVolumeEntry[]>(rows.Count);
        foreach (var (playerId, row) in rows)
        {
            arrays[playerId] = [.. row];
        }

        return new GameVolumeTable(arrays);
    }

    /// <summary>
    /// Builds the table for the connected players, comparing only players within the hearing radius, which the index
    /// finds. The index must be up to date; the caller guards it, and also the <paramref name="candidates"/> list,
    /// which is scratch space.
    /// </summary>
    public static GameVolumeTable Build(Dictionary<string, GamePlayer> players, GameWorldIndex index, List<GameObject> candidates)
    {
        var connectedCount = 0;
        foreach (var player in players.Values)
        {
            if (player.IsConnected)
            {
                connectedCount++;
            }
        }

        var rows = new Dictionary<string, List<GameVolumeEntry>>(connectedCount);
        foreach (var player in players.Values)
        {
            if (player.IsConnected)
            {
                rows[player.ClientId] = [];
            }
        }

        foreach (var player in players.Values)
        {
            if (rows.TryGetValue(player.ClientId, out var row))
            {
                candidates.Clear();
                index.Query(HearingRange.GetArea(player.Footprint), candidates);
                AddAudible(player, row, candidates, rows, index);
            }
        }

        return Create(rows);
    }

    // Computes each pair once, from the player with the smaller id, and adds it to both rows.
    private static void AddAudible(GamePlayer listener, List<GameVolumeEntry> listenerRow, List<GameObject> candidates,
        Dictionary<string, List<GameVolumeEntry>> rows, GameWorldIndex index)
    {
        foreach (var candidate in candidates)
        {
            if (candidate is not GamePlayer speaker || string.CompareOrdinal(listener.ClientId, speaker.ClientId) >= 0
                || !rows.TryGetValue(speaker.ClientId, out var speakerRow))
            {
                continue;
            }

            var volume = (float)HearingRange.GetVolume(listener, speaker, index);
            if (volume > 0)
            {
                listenerRow.Add(new GameVolumeEntry(speaker.ClientId, volume));
                speakerRow.Add(new GameVolumeEntry(listener.ClientId, volume));
            }
        }
    }
}
