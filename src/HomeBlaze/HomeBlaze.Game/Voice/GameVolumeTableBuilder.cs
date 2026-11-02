using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Voice;

/// <summary>
/// Builds volume tables from immutable inputs, keeping the closed area labels and the sound paths of a
/// <see cref="SoundSnapshot"/> version from build to build. Not thread-safe: one build at a time, and only the builds
/// touch it.
/// </summary>
internal sealed class GameVolumeTableBuilder
{
    // Cells of 64 by 64 tiles, so the hearing area of a player covers at most 3 by 3 of them.
    private const int CellShift = 6;

    private readonly SoundMap _map = new();
    private readonly SoundPathSearch _search = new();
    private readonly Dictionary<(long X, long Y), List<int>> _cells = new();
    private readonly Stack<List<int>> _pooledCells = new();
    private readonly List<List<GameVolumeEntry>> _rows = [];
    private int[] _candidateStamps = [];

    /// <summary>
    /// The path search of the builds, for tests of the caches.
    /// </summary>
    internal SoundPathSearch SearchForTest => _search;

    /// <summary>
    /// Builds the table of the players with the sound blocked tiles of the snapshot. The players hear and are heard,
    /// have a valid size and distinct client ids; each gets a row.
    /// </summary>
    public GameVolumeTable Build(ReadOnlySpan<HearingPosition> players, SoundSnapshot sound)
    {
        _map.Update(sound);
        AddToCells(players);
        PrepareRows(players.Length);
        if (_candidateStamps.Length < players.Length)
        {
            _candidateStamps = new int[players.Length];
        }

        Array.Clear(_candidateStamps, 0, players.Length);
        for (var listener = 0; listener < players.Length; listener++)
        {
            AddAudible(players, listener);
        }

        var rows = new Dictionary<string, GameVolumeEntry[]>(players.Length);
        for (var player = 0; player < players.Length; player++)
        {
            rows[players[player].ClientId] = [.. _rows[player]];
        }

        return GameVolumeTable.Wrap(rows);
    }

    /// <summary>
    /// Drops the caches, as after a build that failed in the middle.
    /// </summary>
    public void Reset()
    {
        _map.Reset();
        _search.Reset();
    }

    // Computes each pair once, from the player with the smaller id, and adds it to both rows. The listener goes first,
    // so the paths of its pairs come from one search.
    private void AddAudible(ReadOnlySpan<HearingPosition> players, int listener)
    {
        var area = HearingRange.GetArea(players[listener].Area);
        for (var cellY = (long)area.Y >> CellShift; cellY <= area.Top >> CellShift; cellY++)
        {
            for (var cellX = (long)area.X >> CellShift; cellX <= area.Right >> CellShift; cellX++)
            {
                if (_cells.TryGetValue((cellX, cellY), out var cell))
                {
                    AddAudible(players, listener, area, cell);
                }
            }
        }
    }

    // Marks each talker it compares with the listener's stamp, since a talker may lie in several cells.
    private void AddAudible(ReadOnlySpan<HearingPosition> players, int listener, GameArea area, List<int> cell)
    {
        var position = players[listener];
        var stamp = listener + 1;
        foreach (var talker in cell)
        {
            var talkerPosition = players[talker];
            if (_candidateStamps[talker] == stamp || !talkerPosition.Area.Intersects(area)
                || string.CompareOrdinal(position.ClientId, talkerPosition.ClientId) >= 0)
            {
                continue;
            }

            _candidateStamps[talker] = stamp;
            var volume = (float)HearingRange.GetVolume(position, talkerPosition, _map, _search);
            if (volume > 0)
            {
                _rows[listener].Add(new GameVolumeEntry(talkerPosition.ClientId, volume));
                _rows[talker].Add(new GameVolumeEntry(position.ClientId, volume));
            }
        }
    }

    private void AddToCells(ReadOnlySpan<HearingPosition> players)
    {
        foreach (var cell in _cells.Values)
        {
            cell.Clear();
            _pooledCells.Push(cell);
        }

        _cells.Clear();
        for (var player = 0; player < players.Length; player++)
        {
            var area = players[player].Area;
            for (var cellY = (long)area.Y >> CellShift; cellY <= area.Top >> CellShift; cellY++)
            {
                for (var cellX = (long)area.X >> CellShift; cellX <= area.Right >> CellShift; cellX++)
                {
                    GetCell((cellX, cellY)).Add(player);
                }
            }
        }
    }

    private List<int> GetCell((long X, long Y) key)
    {
        if (!_cells.TryGetValue(key, out var cell))
        {
            cell = _pooledCells.Count > 0 ? _pooledCells.Pop() : [];
            _cells.Add(key, cell);
        }

        return cell;
    }

    private void PrepareRows(int count)
    {
        while (_rows.Count < count)
        {
            _rows.Add([]);
        }

        for (var row = 0; row < count; row++)
        {
            _rows[row].Clear();
        }
    }
}
