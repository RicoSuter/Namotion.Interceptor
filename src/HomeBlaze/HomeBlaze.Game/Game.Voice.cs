using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.Voice;
using HomeBlaze.Game.World;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Game;

public partial class Game
{
    private readonly GameVoiceMixer _voiceMixer;

    // Replaced on the simulation thread only and read by any thread; a published table never changes.
    private volatile GameVolumeTable _volumeTable = GameVolumeTable.Empty;

    // Touched only by the running volume table build. The simulation starts a build only after it saw the previous one
    // complete, so two builds never touch it at once.
    private readonly GameVolumeTableBuilder _volumeTableBuilder = new();

    // Touched only on the simulation thread, and by Start while no tick runs: the running build and the inputs of the
    // last one started.
    private readonly List<HearingPosition> _hearingPositions = [];
    private HearingPosition[] _builtHearingPositions = [];
    private Task<VolumeTableBuild>? _volumeTableBuild;
    private bool _isVolumeTableBuildAbandoned;
    private long _volumeTableIndexVersion = -1;
    private long _volumeTableSoundVersion = -1;
    private long _lastVolumeTableBuildStart;
    private bool _hasVolumeTableBuildStarted;
    private bool _isVolumeTableBuildFailing;

    private volatile Task? _volumeTableBuildBlocker;

    // Replaced on the simulation thread only and read by any thread; a published snapshot never changes.
    private volatile SoundSnapshot _soundSnapshot = SoundSnapshot.Empty;

    // Switched on the simulation thread only.
    private readonly GameVoiceLoop _voiceLoop;

    // Touched only by the voice loop.
    private bool _isVoiceTickFailing;

    /// <summary>
    /// Mixes the voices of the players; a page passes it the frames of its microphone and registers its speaker.
    /// </summary>
    internal GameVoiceMixer VoiceMixer => _voiceMixer;

    /// <summary>
    /// For every connected player, the players it hears and how loud. A published table never changes, so a reader
    /// captures it once per use and works with that one.
    /// </summary>
    internal GameVolumeTable VolumeTable => _volumeTable;

    /// <summary>
    /// Raised on the simulation thread after a new <see cref="VolumeTable"/> was published; handlers must not block or throw.
    /// </summary>
    internal event Action? VolumeTableChanged;

    /// <summary>
    /// Whether the voice loop waits for a player to turn the microphone or the speaker on; any thread may read it.
    /// </summary>
    internal bool IsVoiceLoopPaused => _voiceLoop.IsPaused;

    /// <summary>
    /// The running or last volume table build, which never faults; read it on the thread that runs the ticks.
    /// </summary>
    internal Task VolumeTableBuildTask => _volumeTableBuild ?? Task.CompletedTask;

    /// <summary>
    /// The sound blocked tiles as of the last tick, for benchmarks that measure a build on their own.
    /// </summary>
    internal SoundSnapshot SoundSnapshotForTest => _soundSnapshot;

    /// <summary>
    /// A task that every volume table build awaits before it builds, so a test holds builds or, with a faulted task, fails
    /// them; null builds at once.
    /// </summary>
    internal Task? VolumeTableBuildBlockerForTest
    {
        get => _volumeTableBuildBlocker;
        set => _volumeTableBuildBlocker = value;
    }

    /// <summary>
    /// Turns the microphone of the client's player on for the page, which makes it the only page whose frames count, or
    /// off, which only acts when the page is that page. The page must pass only its own client. Completes in the next tick.
    /// </summary>
    /// <returns>False when the client has no player in this game, or it is disconnected while turning on.</returns>
    internal Task<bool> SetMicrophoneAsync(string clientId, int pageId, bool isOn, CancellationToken cancellationToken = default) =>
        _simulation.InvokeAsync(() => SetMicrophone(clientId, pageId, isOn), cancellationToken);

    /// <summary>
    /// Turns the speaker of the page on or off; the player's audio is on while any of its pages has it on. The page must
    /// pass only its own client. Completes in the next tick.
    /// </summary>
    /// <returns>False when the client has no player in this game, or it is disconnected while turning on.</returns>
    internal Task<bool> SetAudioAsync(string clientId, int pageId, bool isOn, CancellationToken cancellationToken = default) =>
        _simulation.InvokeAsync(() => SetAudio(clientId, pageId, isOn), cancellationToken);

    /// <summary>
    /// Gets whether the page is the microphone page of the client's player and whether it has the speaker on.
    /// </summary>
    internal Task<(bool IsMicrophonePage, bool IsAudioPage)> GetVoicePagesAsync(string clientId, int pageId, CancellationToken cancellationToken = default) =>
        _simulation.InvokeAsync(() => _world.Players.TryGetValue(clientId, out var player)
            ? (player.MicrophonePageId == pageId, player.AudioPageIds?.Contains(pageId) == true)
            : (false, false), cancellationToken);

    /// <summary>
    /// Gets the pages of the client's player that have the speaker on.
    /// </summary>
    internal Task<int[]> GetAudioPagesForTestAsync(string clientId) =>
        _simulation.InvokeAsync(() => _world.Players.TryGetValue(clientId, out var player) && player.AudioPageIds is { } pages
            ? [.. pages]
            : Array.Empty<int>(), CancellationToken.None);

    /// <summary>
    /// Mixes one frame for every listener with the current volume table; logs instead of throwing, so voice never stops
    /// the loop.
    /// </summary>
    internal void TickVoice()
    {
        try
        {
            _voiceMixer.Mix(_volumeTable);
            _isVoiceTickFailing = false;
        }
        catch (Exception exception)
        {
            // Warns when ticks start failing and stays at debug level while they keep failing, so a persistent fault does
            // not flood the log at the frame rate.
            var level = _isVoiceTickFailing ? LogLevel.Debug : LogLevel.Warning;
            _isVoiceTickFailing = true;
            _logger.Log(level, exception, "A voice tick of the game {Game} failed", Title);
        }
    }

    private bool SetMicrophone(string clientId, int pageId, bool isOn)
    {
        if (!TryGetVoicePlayer(clientId, isOn, out var player))
        {
            return false;
        }

        var page = player.MicrophonePageId;
        if (isOn)
        {
            page = pageId;
        }
        else if (page == pageId)
        {
            page = null;
        }

        if (page != player.MicrophonePageId)
        {
            player.MicrophonePageId = page;
            player.IsMicrophoneOn = page is not null;
            _voiceMixer.SetMicrophonePage(clientId, page);
            _world.OnVoiceChanged(player);
        }

        return true;
    }

    private bool SetAudio(string clientId, int pageId, bool isOn)
    {
        if (!TryGetVoicePlayer(clientId, isOn, out var player))
        {
            return false;
        }

        if (!isOn && player.AudioPageIds is null)
        {
            return true;
        }

        var pages = player.AudioPageIds ??= [];
        var isChanged = isOn ? pages.Add(pageId) : pages.Remove(pageId);
        if (isChanged && player.IsAudioOn != (pages.Count > 0))
        {
            player.IsAudioOn = pages.Count > 0;
            _world.OnVoiceChanged(player);
        }

        return true;
    }

    // Turning off works while disconnected, so a closing page always removes its share.
    private bool TryGetVoicePlayer(string clientId, bool isOn, [NotNullWhen(true)] out PlayerEntity? player) =>
        _world.Players.TryGetValue(clientId, out player) && (player.IsConnected || !isOn);

    // Takes the sound changes of the tick into the snapshot, swaps in a finished build, then starts the next one. Never
    // waits for a build, so a slow one only delays the table.
    private void UpdateVolumeTable()
    {
        _soundSnapshot = _soundSnapshot.TakeChanges(_world.Index);
        var isChanged = TryCompleteVolumeTableBuild();
        try
        {
            isChanged |= StartVolumeTableBuildWhenDue();
        }
        finally
        {
            if (isChanged)
            {
                VolumeTableChanged?.Invoke();
            }
        }
    }

    // Returns whether it published a new table.
    private bool TryCompleteVolumeTableBuild()
    {
        if (_volumeTableBuild is not { IsCompleted: true } build)
        {
            return false;
        }

        _volumeTableBuild = null;
        if (_isVolumeTableBuildAbandoned)
        {
            _isVolumeTableBuildAbandoned = false;
            return false;
        }

        var (table, exception) = build.Result;
        if (table is not null)
        {
            _volumeTable = table;
            _isVolumeTableBuildFailing = false;
            return true;
        }

        // Warns when builds start failing and stays at debug level while they keep failing, so a persistent fault does
        // not flood the log at the build rate.
        var level = _isVolumeTableBuildFailing ? LogLevel.Debug : LogLevel.Warning;
        _isVolumeTableBuildFailing = true;
        _logger.Log(level, exception, "Building the volume table of the game {Game} failed and is tried again", Title);
        _volumeTableSoundVersion = -1;
        return false;
    }

    // Starts at most every VolumeTableInterval, and only when the players or the sound blocked tiles changed. A voice
    // switch alone changes no row, so it builds nothing; returns true then, since VolumeTableChanged tells the pages.
    private bool StartVolumeTableBuildWhenDue()
    {
        var now = _timeProvider.GetTimestamp();
        if (_volumeTableBuild is not null
            || (_hasVolumeTableBuildStarted && _timeProvider.GetElapsedTime(_lastVolumeTableBuildStart, now) < GameVoiceSettings.VolumeTableInterval))
        {
            return false;
        }

        var sound = _soundSnapshot;
        var arePositionsChanged = ArePositionsChanged();
        if (!arePositionsChanged && sound.Version == _volumeTableSoundVersion)
        {
            var isVoiceChanged = _world.IsVoiceChanged;
            _world.IsVoiceChanged = false;
            return isVoiceChanged;
        }

        if (arePositionsChanged)
        {
            _builtHearingPositions = _hearingPositions.ToArray();
        }

        _volumeTableSoundVersion = sound.Version;
        _world.IsVoiceChanged = false;
        _lastVolumeTableBuildStart = now;
        _hasVolumeTableBuildStarted = true;
        _volumeTableBuild = StartVolumeTableBuild(_builtHearingPositions, sound);
        return false;
    }

    // The players' areas and connections are in the index, so they are collected and compared only when its version
    // changed; otherwise they equal the positions of the last build.
    private bool ArePositionsChanged()
    {
        var indexVersion = _world.Index.Version;
        if (indexVersion == _volumeTableIndexVersion)
        {
            return false;
        }

        _volumeTableIndexVersion = indexVersion;
        CollectHearingPositions();
        return !CollectionsMarshal.AsSpan(_hearingPositions).SequenceEqual(_builtHearingPositions);
    }

    // Its own method, so the closure is allocated only when a build starts.
    private Task<VolumeTableBuild> StartVolumeTableBuild(HearingPosition[] positions, SoundSnapshot sound) =>
        Task.Run(() => BuildVolumeTableAsync(positions, sound));

    // A player of a size that the index leaves out hears nobody and is heard by nobody, so its huge area is never searched.
    private void CollectHearingPositions()
    {
        _hearingPositions.Clear();
        foreach (var player in _world.Players.Values)
        {
            if (player.IsConnected && GameWorldIndex.HasValidSize(player.Area, player.Height))
            {
                _hearingPositions.Add(new HearingPosition(player.ClientId, player.Area));
            }
        }
    }

    // Runs on the thread pool and never throws, so an abandoned build leaves no unobserved exception.
    private async Task<VolumeTableBuild> BuildVolumeTableAsync(HearingPosition[] positions, SoundSnapshot sound)
    {
        try
        {
            if (_volumeTableBuildBlocker is { } blocker)
            {
                await blocker.ConfigureAwait(false);
            }

            return new VolumeTableBuild(_volumeTableBuilder.Build(positions, sound), null);
        }
        catch (Exception exception)
        {
            _volumeTableBuilder.Reset();
            return new VolumeTableBuild(null, exception);
        }
    }

    // A build that the stop left running or unpublished is dropped once it completes, and the next one starts afresh
    // from an empty table; until it completes, none starts, so the builder stays with one build.
    private void ResetVolumeTableBuild()
    {
        _isVolumeTableBuildAbandoned = _volumeTableBuild is not null;
        _volumeTable = GameVolumeTable.Empty;
        _hearingPositions.Clear();
        _builtHearingPositions = [];
        _volumeTableIndexVersion = -1;
        _volumeTableSoundVersion = -1;
        _hasVolumeTableBuildStarted = false;
        _isVolumeTableBuildFailing = false;
    }

    private GameVoiceLoop CreateVoiceLoop() => new(_voiceMixer, _timeProvider, TickVoice);

    // Lets the voice loop run while a player has the microphone or the speaker on; with all of them off nothing is mixed
    // or sent, so the loop pauses without changing what anybody hears.
    private void UpdateVoiceLoop() => _voiceLoop.SetActive(_world.HasVoiceOn);

    private readonly record struct VolumeTableBuild(GameVolumeTable? Table, Exception? Exception);
}
