using HomeBlaze.Game.Voice;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Game;

public partial class Game
{
    private readonly GameVoiceMixer _voiceMixer;

    // Guarded by _lock: the players with a voice toggle on by client id, so that an outside replacement of Players
    // finds the instances whose state and mixer speaker it orphaned.
    private readonly Dictionary<string, GamePlayer> _voicePlayers = new();

    /// <summary>
    /// Mixes the voices of the players; a page passes it the frames of its microphone and registers its speaker.
    /// </summary>
    internal GameVoiceMixer VoiceMixer => _voiceMixer;

    /// <summary>
    /// Turns the microphone of the caller's player on for the page, which makes it the only page whose frames count, or
    /// off, which only acts when the page is that page. The caller must pass only the player of its own client.
    /// </summary>
    /// <returns>False when the player is not a player of this game, or is disconnected while turning on.</returns>
    internal bool SetMicrophone(GameCaller caller, int pageId, bool isOn)
    {
        lock (_lock)
        {
            var player = caller.Player;
            if (!CanSetVoice(player, isOn))
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
                _voiceMixer.SetMicrophonePage(player.ClientId, page);
                TrackVoice(player);
                _isVolumeTableDirty = true;
            }

            return true;
        }
    }

    /// <summary>
    /// Turns the speaker of the page on or off; the player's audio is on while any of its pages has it on. The caller
    /// must pass only the player of its own client.
    /// </summary>
    /// <returns>False when the player is not a player of this game, or is disconnected while turning on.</returns>
    internal bool SetAudio(GameCaller caller, int pageId, bool isOn)
    {
        lock (_lock)
        {
            var player = caller.Player;
            if (!CanSetVoice(player, isOn))
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
                TrackVoice(player);
                _isVolumeTableDirty = true;
            }

            return true;
        }
    }

    /// <summary>
    /// Rebuilds the volume table when it is due and mixes one frame for every listener; logs instead of throwing, so
    /// voice never stops the loop.
    /// </summary>
    internal void TickVoice()
    {
        try
        {
            UpdateVolumeTable();
            _voiceMixer.Mix(_volumeTable);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "A voice tick of the game {Game} failed", Title);
        }
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

    /// <summary>
    /// Gets whether the page is the player's microphone page and whether it has the speaker on.
    /// </summary>
    internal (bool IsMicrophonePage, bool IsAudioPage) GetVoicePages(GamePlayer player, int pageId)
    {
        lock (_lock)
        {
            return (player.MicrophonePageId == pageId, player.AudioPageIds?.Contains(pageId) == true);
        }
    }

    // Callers hold _lock. Turning off works while disconnected, so a closing page always removes its share.
    private bool CanSetVoice(GamePlayer player, bool isOn) =>
        IsPlayerOfThisGame(player) && (player.IsConnected || !isOn);

    // Callers hold _lock. Turns the voice of a player off that disconnected.
    private void ResetVoice(GamePlayer player)
    {
        if (!ClearVoice(player))
        {
            return;
        }

        _voiceMixer.SetMicrophonePage(player.ClientId, null);
        _voicePlayers.Remove(player.ClientId);
        _isVolumeTableDirty = true;
    }

    // Callers hold _lock. Returns whether the player had a voice toggle on.
    private static bool ClearVoice(GamePlayer player)
    {
        if (player.MicrophonePageId is null && player.AudioPageIds is not { Count: > 0 })
        {
            return false;
        }

        player.MicrophonePageId = null;
        player.AudioPageIds?.Clear();
        player.IsMicrophoneOn = false;
        player.IsAudioOn = false;
        return true;
    }

    // Callers hold _lock. Keeps the instance of a player listed while any toggle is on; the player is current.
    private void TrackVoice(GamePlayer player)
    {
        if (player.MicrophonePageId is not null || player.AudioPageIds is { Count: > 0 })
        {
            _voicePlayers[player.ClientId] = player;
        }
        else
        {
            _voicePlayers.Remove(player.ClientId);
        }
    }

    // Callers hold _lock. Turns the voice off of every listed instance that Players no longer holds, because the player
    // left or an outside assignment replaced it, and leaves the speaker of the instance that took its place alone.
    private void ReleaseVoiceOfReplacedPlayers()
    {
        foreach (var (id, listed) in _voicePlayers)
        {
            CurrentPlayers.TryGetValue(id, out var current);
            if (ReferenceEquals(current, listed))
            {
                continue;
            }

            ClearVoice(listed);
            _voicePlayers.Remove(id);
            _voiceMixer.SetMicrophonePage(id, current?.MicrophonePageId);
            _isVolumeTableDirty = true;
        }
    }
}
