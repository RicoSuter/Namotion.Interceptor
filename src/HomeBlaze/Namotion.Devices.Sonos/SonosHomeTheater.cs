using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Sonos.Parsing;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Devices.Sonos;

/// <summary>
/// The home theater settings of a room player. Only players whose device description lists <c>HTControl</c>, such as
/// soundbars, have one, see <see cref="SonosPlayer.HomeTheater"/>.
/// </summary>
[InterceptorSubject]
public partial class SonosHomeTheater :
    ITitleProvider,
    IIconProvider
{
    private readonly SonosPlayer _player;

    internal SonosHomeTheater(SonosPlayer player)
    {
        _player = player;
        NightMode = null;
        SpeechEnhancement = null;
    }

    [State(Position = 1)]
    public partial bool? NightMode { get; internal set; }

    /// <summary>
    /// Whether speech enhancement is on. Any dialog level above zero counts as on.
    /// </summary>
    [State(Position = 2)]
    public partial bool? SpeechEnhancement { get; internal set; }

    [Derived]
    public string? Title => $"Home Theater ({_player.RoomName})";

    [Derived]
    public string? IconName => "Tv";

    [Derived]
    [PropertyAttribute("SetNightMode", KnownAttributes.IsEnabled)]
    public bool SetNightMode_IsEnabled => _player.CanControl;

    [Operation(Title = "Set Night Mode", Position = 1, Description = "Turns night mode on or off for this home theater player.")]
    public Task SetNightModeAsync(bool nightMode, CancellationToken cancellationToken) =>
        _player.RunOnPlayerAsync((connection, token) => connection.SetEqualizerAsync("NightMode", nightMode, token), cancellationToken);

    [Derived]
    [PropertyAttribute("SetSpeechEnhancement", KnownAttributes.IsEnabled)]
    public bool SetSpeechEnhancement_IsEnabled => _player.CanControl;

    [Operation(Title = "Set Speech Enhancement", Position = 2, Description = "Turns speech enhancement on or off for this home theater player.")]
    public Task SetSpeechEnhancementAsync(bool speechEnhancement, CancellationToken cancellationToken) =>
        _player.RunOnPlayerAsync((connection, token) => connection.SetEqualizerAsync("DialogLevel", speechEnhancement, token), cancellationToken);

    /// <summary>
    /// Applies the settings an event or poll reported. The caller holds the player's state lock, which orders the
    /// two against each other.
    /// </summary>
    internal void Apply(RenderingControlChange change)
    {
        if (change.NightMode is { } nightMode)
        {
            NightMode = nightMode;
        }

        if (change.SpeechEnhancement is { } speechEnhancement)
        {
            SpeechEnhancement = speechEnhancement;
        }
    }
}
