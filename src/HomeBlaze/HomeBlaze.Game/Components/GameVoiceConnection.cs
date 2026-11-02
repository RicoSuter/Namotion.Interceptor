using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.Voice;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace HomeBlaze.Game.Components;

/// <summary>
/// The voice of a page: its microphone and speaker toggles, their share of the player's toggles in the game, its speaker
/// in the game's mixer and the voice module in the browser. Voice never breaks the page: a call that fails only leaves the
/// voice of the page off. Runs on the renderer, except <see cref="IGameVoiceSink.Send"/>, which the mixer calls.
/// </summary>
internal sealed class GameVoiceConnection : IGameVoiceSink, IAsyncDisposable
{
    private const string ResumeMessage = "Tap the microphone or speaker to resume";
    private const string MicrophoneEndedMessage = "The microphone was disconnected";
    private const string NotConnectedMessage = "Voice is not available while you are not connected";

    private readonly IJSRuntime _jsRuntime;
    private readonly ILogger _logger;
    private readonly Game _game;
    private readonly int _viewId;
    private readonly Func<Func<Task>, Task> _invokeAsync;
    private readonly CancellationTokenSource _lifetime = new();
    // Read from the token source once, so calls that run after the disposal see it canceled rather than disposed.
    private readonly CancellationToken _lifetimeToken;
    private GameVoiceModule? _module;
    private IDisposable? _sinkRegistration;
    private bool _isResumeTried;
    private bool _isCaptureEnded;
    private bool _isSynchronizationRequested;
    // Whether a toggle runs; the toggles ignore clicks meanwhile.
    private bool _isBusy;
    // Why the last toggle left the voice off, if it did.
    private string? _error;
    private bool _isDisposed;

    /// <param name="jsRuntime">The JS runtime of the page.</param>
    /// <param name="logger">Logs failed voice calls.</param>
    /// <param name="game">The game the page shows.</param>
    /// <param name="viewId">The id of the page's view, which also identifies its voice.</param>
    /// <param name="invokeAsync">Runs work on the page's renderer.</param>
    public GameVoiceConnection(IJSRuntime jsRuntime, ILogger logger, Game game, int viewId, Func<Func<Task>, Task> invokeAsync)
    {
        _jsRuntime = jsRuntime;
        _logger = logger;
        _game = game;
        _viewId = viewId;
        _invokeAsync = invokeAsync;
        _lifetimeToken = _lifetime.Token;
    }

    /// <summary>
    /// The page's player, which the toggles switch; null while the page has none.
    /// </summary>
    public GamePlayer? Player { get; set; }

    /// <summary>
    /// Null until the voice module answered, false where the browser offers no voice.
    /// </summary>
    public bool? IsSupported { get; private set; }

    public bool IsMicrophoneOn { get; private set; }

    public bool IsAudioOn { get; private set; }

    /// <summary>
    /// Whether the browser suspended the audio context, which only a click resumes.
    /// </summary>
    public bool IsAudioSuspended { get; private set; }

    /// <summary>
    /// The message the panel shows about the voice, if any.
    /// </summary>
    public string? Message => IsAudioSuspended && (IsAudioOn || IsMicrophoneOn) ? ResumeMessage : _error;

    /// <summary>
    /// Raised on the renderer when the state of the toggles changed.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Loads the voice module; a browser that cannot load it keeps the game and leaves the toggles unsupported.
    /// </summary>
    public async Task StartAsync(object dotNetReference, ElementReference panel)
    {
        try
        {
            var module = await GameVoiceModule.ImportAsync(_jsRuntime, _viewId, _logger, _invokeAsync, _lifetimeToken);
            if (_isDisposed)
            {
                await module.DisposeAsync();
                return;
            }

            _module = module;
            IsSupported = await module.InitializeAsync(dotNetReference, panel);
        }
        catch (JSException exception)
        {
            _logger.LogDebug(exception, "The voice of the game could not be loaded");
        }

        RaiseChanged();
    }

    public Task ToggleMicrophoneAsync() => ToggleAsync(isMicrophone: true);

    public Task ToggleAudioAsync() => ToggleAsync(isMicrophone: false);

    /// <summary>
    /// Turns the microphone off after the browser lost its track, as when the device was unplugged.
    /// </summary>
    public Task OnCaptureEndedAsync()
    {
        _isCaptureEnded = true;
        return StopEndedCaptureAsync();
    }

    public void OnAudioStateChanged(bool isSuspended)
    {
        IsAudioSuspended = isSuspended;
        _isResumeTried &= isSuspended;
        RaiseChanged();
    }

    /// <summary>
    /// Passes a frame of the page's microphone to the mixer, which drops it unless this page is the microphone page.
    /// </summary>
    public void Receive(byte[] frame)
    {
        if (Player is { } player)
        {
            _game.VoiceMixer.Receive(player.ClientId, _viewId, frame);
        }
    }

    /// <summary>
    /// Turns off what the game no longer gives this page, after another page of the player took the microphone over, or a
    /// disconnect turned the toggles of every page off. Call after the volume table changed.
    /// </summary>
    public async Task SynchronizeAsync()
    {
        if ((!IsMicrophoneOn && !IsAudioOn) || _module is not { } module)
        {
            return;
        }

        if (_isBusy)
        {
            // RunToggleAsync synchronizes once the running toggle ended.
            _isSynchronizationRequested = true;
            return;
        }

        _isSynchronizationRequested = false;
        var (isMicrophonePage, isAudioPage) = Player is { } player ? await TryGetVoicePagesAsync(player.ClientId) : (false, false);
        var isMicrophoneLost = IsMicrophoneOn && !isMicrophonePage;
        var isAudioLost = IsAudioOn && !isAudioPage;
        if (isMicrophoneLost || isAudioLost)
        {
            await RunToggleAsync(() => StopLostVoiceAsync(module, isMicrophoneLost, isAudioLost));
        }
    }

    /// <summary>
    /// Switches off this page's share of the player's toggles, which the other pages of the player keep, drops the page's
    /// speaker in the game's mixer and clears <see cref="Player"/>, so a toggle that still runs leaves the game alone.
    /// Does not wait for the game, which switches the toggles in its next tick.
    /// </summary>
    public void StopInGame()
    {
        _sinkRegistration?.Dispose();
        _sinkRegistration = null;
        if (Player is { } player)
        {
            _ = TrySetVoiceAsync(_game.SetMicrophoneAsync(player.ClientId, _viewId, isOn: false, CancellationToken.None));
            _ = TrySetVoiceAsync(_game.SetAudioAsync(player.ClientId, _viewId, isOn: false, CancellationToken.None));
            Player = null;
        }
    }

    /// <summary>
    /// Stops the voice in the game and in the browser.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        StopInGame();
        try
        {
            await _lifetime.CancelAsync();
            if (_module is { } module)
            {
                await module.DisposeAsync();
            }
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    // Called on the voice loop.
    void IGameVoiceSink.Send(byte[] frame)
    {
        if (!_isDisposed)
        {
            _module?.Play(frame);
        }
    }

    private void RaiseChanged()
    {
        if (!_isDisposed)
        {
            Changed?.Invoke();
        }
    }

    private bool CanToggle() => !_isDisposed && !_isBusy && IsSupported == true;

    // Turning off works without a player, so a page that lost its player can still stop a live capture.
    private async Task ToggleAsync(bool isMicrophone)
    {
        if (!CanToggle() || _module is not { } module)
        {
            return;
        }

        var isOn = isMicrophone ? IsMicrophoneOn : IsAudioOn;
        if (await TryResumeAsync(module, isOn))
        {
            return;
        }

        if (isOn)
        {
            await RunToggleAsync(() => isMicrophone ? StopMicrophoneAsync(module, Player) : StopAudioAsync(module, Player));
        }
        else if (Player is { } player)
        {
            await RunToggleAsync(() => isMicrophone ? StartMicrophoneAsync(module, player) : StartAudioAsync(module, player));
        }
    }

    // The click already resumed the audio where the browser allows it, so the first tap on a toggle that is on only asks
    // once more; a second tap turns it off, also when the browser keeps the audio suspended.
    private async Task<bool> TryResumeAsync(GameVoiceModule module, bool isOn)
    {
        if (!isOn || !IsAudioSuspended || _isResumeTried)
        {
            return false;
        }

        _isResumeTried = true;
        await RunToggleAsync(() => module.TryInvokeAsync("resume", GameVoiceSettings.BrowserCallTimeout));
        return true;
    }

    private async Task RunToggleAsync(Func<Task> toggle)
    {
        _isBusy = true;
        try
        {
            _error = null;
            RaiseChanged();
            await toggle();
        }
        finally
        {
            _isBusy = false;
        }

        if (_isDisposed)
        {
            return;
        }

        RaiseChanged();
        // A volume table change that arrived during the toggle left the synchronization to this.
        if (_isSynchronizationRequested)
        {
            await SynchronizeAsync();
        }

        await StopEndedCaptureAsync();
    }

    // A capture that ended while a toggle ran waits for RunToggleAsync, which calls this again once it is done.
    private async Task StopEndedCaptureAsync()
    {
        if (!_isCaptureEnded || !CanToggle() || _module is not { } module)
        {
            return;
        }

        _isCaptureEnded = false;
        if (IsMicrophoneOn)
        {
            await RunToggleAsync(async () =>
            {
                await StopMicrophoneAsync(module, Player);
                _error = MicrophoneEndedMessage;
            });
        }
    }

    // The game hears the page only once the browser captures.
    private async Task StartMicrophoneAsync(GameVoiceModule module, GamePlayer player)
    {
        var result = await module.TryStartAsync("startCapture", "stopCapture", GameVoiceSettings.CaptureStartTimeout);
        if (!result.IsStarted)
        {
            _error = GetMicrophoneError(result.Error);
            return;
        }

        if (_isDisposed)
        {
            return;
        }

        var isOn = await TrySetVoiceAsync(_game.SetMicrophoneAsync(player.ClientId, _viewId, isOn: true, _lifetimeToken));
        if (isOn && IsPlayerKept(player))
        {
            IsMicrophoneOn = true;
            return;
        }

        await SwitchBackOffAsync(isOn, player, isMicrophone: true);
        await module.TryInvokeAsync("stopCapture", GameVoiceSettings.BrowserCallTimeout);
    }

    // Without a player, the game already turned the voice of the page's former player off.
    private async Task StopMicrophoneAsync(GameVoiceModule module, GamePlayer? player)
    {
        IsMicrophoneOn = false;
        if (player is not null)
        {
            await TrySetVoiceAsync(_game.SetMicrophoneAsync(player.ClientId, _viewId, isOn: false, CancellationToken.None));
        }

        await module.TryInvokeAsync("stopCapture", GameVoiceSettings.BrowserCallTimeout);
    }

    private async Task StartAudioAsync(GameVoiceModule module, GamePlayer player)
    {
        var result = await module.TryStartAsync("startPlayback", "stopPlayback", GameVoiceSettings.BrowserCallTimeout);
        if (!result.IsStarted)
        {
            _error = "The speaker could not be started";
            return;
        }

        if (_isDisposed)
        {
            return;
        }

        var registration = _game.VoiceMixer.RegisterSink(player.ClientId, this);
        var isOn = await TrySetVoiceAsync(_game.SetAudioAsync(player.ClientId, _viewId, isOn: true, _lifetimeToken));
        if (isOn && IsPlayerKept(player))
        {
            _sinkRegistration = registration;
            IsAudioOn = true;
            return;
        }

        registration.Dispose();
        await SwitchBackOffAsync(isOn, player, isMicrophone: false);
        await module.TryInvokeAsync("stopPlayback", GameVoiceSettings.BrowserCallTimeout);
    }

    private async Task StopAudioAsync(GameVoiceModule module, GamePlayer? player)
    {
        IsAudioOn = false;
        if (player is not null)
        {
            await TrySetVoiceAsync(_game.SetAudioAsync(player.ClientId, _viewId, isOn: false, CancellationToken.None));
        }

        _sinkRegistration?.Dispose();
        _sinkRegistration = null;
        await module.TryInvokeAsync("stopPlayback", GameVoiceSettings.BrowserCallTimeout);
    }

    private async Task StopLostVoiceAsync(GameVoiceModule module, bool isMicrophoneLost, bool isAudioLost)
    {
        if (isMicrophoneLost)
        {
            await StopMicrophoneAsync(module, Player);
        }

        if (isAudioLost)
        {
            await StopAudioAsync(module, Player);
        }
    }

    // False after a disposal or a change of the player during a switch on: StopInGame found the toggle still off then.
    private bool IsPlayerKept(GamePlayer player) => !_isDisposed && ReferenceEquals(Player, player);

    // Switches a toggle that the game turned on back off, or shows why the game refused it.
    private async Task SwitchBackOffAsync(bool isOn, GamePlayer player, bool isMicrophone)
    {
        if (isOn)
        {
            await TrySetVoiceAsync(isMicrophone
                ? _game.SetMicrophoneAsync(player.ClientId, _viewId, isOn: false, CancellationToken.None)
                : _game.SetAudioAsync(player.ClientId, _viewId, isOn: false, CancellationToken.None));
        }
        else
        {
            _error = NotConnectedMessage;
        }
    }

    // Reads as no page once the game has stopped, which stops the page's voice.
    private async Task<(bool IsMicrophonePage, bool IsAudioPage)> TryGetVoicePagesAsync(string clientId)
    {
        try
        {
            return await _game.GetVoicePagesAsync(clientId, _viewId, _lifetimeToken);
        }
        catch (Exception exception) when (exception is GameStoppedException or OperationCanceledException)
        {
            return (false, false);
        }
    }

    // Returns false once the game has stopped or the page went away, as for a refused switch.
    private static async Task<bool> TrySetVoiceAsync(Task<bool> switching)
    {
        try
        {
            return await switching;
        }
        catch (Exception exception) when (exception is GameStoppedException or OperationCanceledException)
        {
            return false;
        }
    }

    private static string GetMicrophoneError(string? error) => error switch
    {
        "denied" => "Microphone access was denied",
        "not-found" => "No microphone was found",
        _ => "The microphone could not be started"
    };
}
