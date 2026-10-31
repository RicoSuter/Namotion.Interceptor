using HomeBlaze.Game.Voice;

namespace HomeBlaze.Game;

public partial class Game
{
    private readonly GameVoiceMixer _voiceMixer;

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

            var pages = player.AudioPageIds ??= [];
            var isChanged = isOn ? pages.Add(pageId) : pages.Remove(pageId);
            if (isChanged && player.IsAudioOn != (pages.Count > 0))
            {
                player.IsAudioOn = pages.Count > 0;
                _isVolumeTableDirty = true;
            }

            return true;
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

    // Callers hold _lock. Turns the voice of a player off that disconnected or left the game.
    private void ResetVoice(GamePlayer player)
    {
        if (player.MicrophonePageId is null && player.AudioPageIds is not { Count: > 0 })
        {
            return;
        }

        player.MicrophonePageId = null;
        player.AudioPageIds?.Clear();
        player.IsMicrophoneOn = false;
        player.IsAudioOn = false;
        _voiceMixer.SetMicrophonePage(player.ClientId, null);
        _isVolumeTableDirty = true;
    }

    // Callers hold _lock.
    private void ResetVoiceOfRemovedPlayers(Dictionary<string, GamePlayer> current, Dictionary<string, GamePlayer> next)
    {
        foreach (var (id, player) in current)
        {
            if (!next.ContainsKey(id))
            {
                ResetVoice(player);
            }
        }
    }
}
