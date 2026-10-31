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

    // Guarded by _lock: the index version and the time of the last rebuild.
    private long _volumeTableIndexVersion = -1;
    private long? _lastVolumeTableRebuild;

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
        lock (_lock)
        {
            var index = GetIndex();
            var now = _timeProvider.GetTimestamp();
            if (!IsVolumeTableDue(index, now))
            {
                return false;
            }

            _volumeTable = GameVolumeTable.Build(Players, index, _volumeCandidates);
            _isVolumeTableDirty = false;
            _volumeTableIndexVersion = index.Version;
            _lastVolumeTableRebuild = now;
            return true;
        }
    }

    // Callers hold _lock.
    private bool IsVolumeTableDue(GameWorldIndex index, long now)
    {
        if (!_isVolumeTableDirty && index.Version == _volumeTableIndexVersion)
        {
            return false;
        }

        return _lastVolumeTableRebuild is not { } last
            || _timeProvider.GetElapsedTime(last, now) >= GameVoiceSettings.VolumeTableInterval;
    }
}
