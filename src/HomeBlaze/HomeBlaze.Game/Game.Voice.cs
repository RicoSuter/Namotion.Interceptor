using System.Diagnostics.CodeAnalysis;
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

    // Touched only on the simulation thread: scratch space and state of the volume table rebuild.
    private readonly List<GameEntity> _volumeCandidates = [];
    private readonly SoundPathSearch _soundPathSearch = new();
    private long _volumeTableIndexVersion = -1;
    private long _lastVolumeTableRebuild;
    private bool _hasVolumeTableRebuilt;

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

    // Rebuilds at most every VolumeTableInterval, and only when the index or a voice switch changed: the rows ignore
    // the switches, but VolumeTableChanged tells the pages about them.
    private void RebuildVolumeTableWhenDue()
    {
        var now = _timeProvider.GetTimestamp();
        if (_hasVolumeTableRebuilt && _timeProvider.GetElapsedTime(_lastVolumeTableRebuild, now) < GameVoiceSettings.VolumeTableInterval)
        {
            return;
        }

        if (!_world.IsVoiceChanged && _world.Index.Version == _volumeTableIndexVersion)
        {
            return;
        }

        _volumeTable = GameVolumeTable.Build(_world.Players, _world.Index, _volumeCandidates, _soundPathSearch);
        _world.IsVoiceChanged = false;
        _volumeTableIndexVersion = _world.Index.Version;
        _lastVolumeTableRebuild = now;
        _hasVolumeTableRebuilt = true;
        VolumeTableChanged?.Invoke();
    }

    // Ticks every frame duration until canceled.
    private async Task RunVoiceLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(GameVoiceSettings.FrameDuration, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                TickVoice();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The game stops.
        }
    }
}
