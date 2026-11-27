using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Voice;

/// <summary>
/// A player that a listener hears, and how loud, above 0 up to 1.
/// </summary>
internal readonly record struct GameVolumeEntry(string PlayerId, float Volume);

/// <summary>
/// For every connected player, the players it hears and their volume, by <see cref="HearingRange"/>;
/// <see cref="GameVolumeTableBuilder"/> builds it. Never changed after it is built, so any thread may read it.
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
    /// Gets the players the player hears; empty for a player without a row.
    /// </summary>
    public ReadOnlySpan<GameVolumeEntry> GetRow(string playerId) =>
        _rows.TryGetValue(playerId, out var row) ? row : ReadOnlySpan<GameVolumeEntry>.Empty;

    /// <summary>
    /// Creates a table that takes over the rows, which nobody may change afterwards.
    /// </summary>
    internal static GameVolumeTable Wrap(Dictionary<string, GameVolumeEntry[]> rows) => new(rows);
}
