using HomeBlaze.Game.Voice;
using HomeBlaze.Game.World;

namespace HomeBlaze.Game;

public partial class Game
{
    // Replaced under _lock only and read without it; a published table never changes.
    private volatile GameVolumeTable _volumeTable = GameVolumeTable.Empty;

    // Guarded by _lock: set by the voice toggles, which do not change the index.
    private bool _isVolumeTableDirty = true;

    // Guarded by _lock: scratch space of the rebuild.
    private readonly List<GameObject> _volumeCandidates = [];
    private readonly SoundPathSearch _soundPathSearch = new();

    // Guarded by _lock: the index version of the last rebuild.
    private long _volumeTableIndexVersion = -1;

    // Written under _lock only, with the timestamp before the flag; read without it by the gate before the lock.
    private long _lastVolumeTableRebuild;
    private bool _hasVolumeTableRebuilt;

    /// <summary>
    /// For every connected player, the players it hears and how loud. A published table never changes, so a reader
    /// captures it once per use and works with that one.
    /// </summary>
    internal GameVolumeTable VolumeTable => _volumeTable;

    /// <summary>
    /// Raised after a new <see cref="VolumeTable"/> was published, outside the game's lock; handlers must not block or throw.
    /// </summary>
    internal event Action? VolumeTableChanged;

    /// <summary>
    /// Rebuilds the volume table when the index changed or a toggle was switched and at least
    /// <see cref="GameVoiceSettings.VolumeTableInterval"/> passed since the last rebuild, then raises
    /// <see cref="VolumeTableChanged"/>.
    /// </summary>
    /// <returns>Whether the table was rebuilt.</returns>
    internal bool UpdateVolumeTable()
    {
        if (!TryRebuildVolumeTable())
        {
            return false;
        }

        VolumeTableChanged?.Invoke();
        return true;
    }

    private bool TryRebuildVolumeTable()
    {
        // The gate runs before the lock and before GetIndex, so a stale index is not rebuilt on every tick.
        var now = _timeProvider.GetTimestamp();
        if (!IsVolumeTableIntervalOver(now))
        {
            return false;
        }

        lock (_lock)
        {
            var index = GetIndex();
            if (!_isVolumeTableDirty && index.Version == _volumeTableIndexVersion)
            {
                return false;
            }

            _volumeTable = GameVolumeTable.Build(Players, index, _volumeCandidates, _soundPathSearch);
            _isVolumeTableDirty = false;
            _volumeTableIndexVersion = index.Version;
            Volatile.Write(ref _lastVolumeTableRebuild, now);
            Volatile.Write(ref _hasVolumeTableRebuilt, true);
            return true;
        }
    }

    private bool IsVolumeTableIntervalOver(long now) =>
        !Volatile.Read(ref _hasVolumeTableRebuilt)
        || _timeProvider.GetElapsedTime(Volatile.Read(ref _lastVolumeTableRebuild), now) >= GameVoiceSettings.VolumeTableInterval;
}
