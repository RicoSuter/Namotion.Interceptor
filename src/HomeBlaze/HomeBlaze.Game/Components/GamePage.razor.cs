using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.View;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Namotion.Interceptor;

namespace HomeBlaze.Game.Components;

public partial class GamePage
{
    private const string JoiningMessage = "Joining the game…";
    private const string RenameTitle = "Change my name";
    private static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(50);

    // In the order of the pad's 3 by 3 grid; the middle one toggles turning only.
    private static readonly PadButton[] PadButtons =
    [
        new("north-west", "Move north west", true), new("north", "Move north", false), new("north-east", "Move north east", true),
        new("west", "Move west", false), new(null, "Turn only", false), new("east", "Move east", false),
        new("south-west", "Move south west", true), new("south", "Move south", false), new("south-east", "Move south east", true),
    ];

    private readonly CancellationTokenSource _lifetime = new();
    private ElementReference _canvas;
    private ElementReference _pad;
    private ElementReference _panel;
    private DotNetObjectReference<GamePage>? _reference;
    // The game the page started or is starting for; null after the page left it, so the next render starts it again.
    private Game? _startedGame;
    private int _layoutKey;
    // The game whose events this page handles, so it unsubscribes from that one also after Subject changed.
    private Game? _subscribedGame;
    private GameViewConnection? _view;
    private GameVoiceConnection? _voice;
    // The disposal of the connections the page left, which a start awaits, so the browser dropped the former view first.
    private Task _detaching = Task.CompletedTask;
    // The page's client and its player, resolved on every flush; the client id is the player's key in Game.Players.
    private string? _clientId;
    private GamePlayer? _player;
    private bool _isStarted;
    private bool _isDisposed;
    private string? _error;
    private int _isFlushScheduled;
    // Flushes run one at a time on the renderer, so at most one update is on its way to the browser.
    private bool _isFlushing;
    private bool _isFlushRequested;
    private string _positionText = GamePanelState.NoPosition;
    private IReadOnlyList<NearbyEntry> _nearby = [];
    // The player and the player ids of the volume table row that _nearby shows.
    private GamePlayer? _nearbyPlayer;
    private string[] _nearbyPlayerIds = [];
    // The publish of the subjects that _nearby shows, whose titles and toggles it reads.
    private long _nearbyPublishVersion = -1;
    private int _isVolumeTableChanged;
    private IReadOnlyList<TargetEntry> _targets = [];
    private IReadOnlyList<GameAction> _actions = [];
    // The parameters this page entered last per operation, keyed by object type and operation, so that the next dialog
    // of the same operation, also on another object of that type, starts from them.
    private readonly Dictionary<string, IReadOnlyDictionary<string, object?>> _lastParameters = new();
    private bool _isInvoking;
    private bool _isRenaming;

    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    private Game? Game => Subject as Game;

    protected override void OnParametersSet()
    {
        // A page shown for another game leaves the former one at once; OnAfterRenderAsync starts it for the new one.
        if (_startedGame is not null && !ReferenceEquals(_startedGame, Subject))
        {
            Detach();
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_isDisposed && Game is { } game && !ReferenceEquals(game, _startedGame))
        {
            await StartAsync(game);
        }
    }

    /// <summary>
    /// Moves the player of this page; returns null while the page has none, which the browser treats as an unanswered step.
    /// </summary>
    [JSInvokable]
    public async Task<StepResult?> Step(int sequence, int direction, bool turnOnly)
    {
        if (_view is not { } view || _player is null || _clientId is not { } clientId)
        {
            return null;
        }

        return await view.StepAsync(clientId, (GameDirection)direction, turnOnly) is { } result
            ? new StepResult(sequence, result.IsAccepted, result.X, result.Y, (int)result.Facing)
            : null;
    }

    /// <summary>
    /// Invokes the target action with the zero-based number key index, as the panel numbers them.
    /// </summary>
    [JSInvokable]
    public Task InvokeTargetAction(int index)
    {
        foreach (var target in _targets)
        {
            foreach (var action in target.Actions)
            {
                if (action.Key == index + 1)
                {
                    return InvokeActionAsync(target.Subject, action.Method);
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Passes a frame of this page's microphone to the mixer, which drops it unless this page is the microphone page.
    /// </summary>
    [JSInvokable]
    public void ReceiveMicrophoneFrame(byte[] frame) => _voice?.Receive(frame);

    /// <summary>
    /// Toggles the microphone from the M key.
    /// </summary>
    [JSInvokable]
    public Task ToggleMicrophone() => ToggleMicrophoneAsync();

    /// <summary>
    /// Toggles the speaker from the N key.
    /// </summary>
    [JSInvokable]
    public Task ToggleAudio() => ToggleAudioAsync();

    /// <summary>
    /// Turns the microphone off after the browser lost its track, as when the device was unplugged.
    /// </summary>
    [JSInvokable]
    public Task OnCaptureEnded() => _voice?.OnCaptureEndedAsync() ?? Task.CompletedTask;

    /// <summary>
    /// Shows whether the browser suspended the audio context, which only a click resumes.
    /// </summary>
    [JSInvokable]
    public void OnAudioStateChanged(bool isSuspended) => _voice?.OnAudioStateChanged(isSuspended);

    private Task ToggleMicrophoneAsync() => _voice?.ToggleMicrophoneAsync() ?? Task.CompletedTask;

    private Task ToggleAudioAsync() => _voice?.ToggleAudioAsync() ?? Task.CompletedTask;

    // Waits for the connections of a former game to be disposed; a start for another game or a disposal meanwhile ends
    // this one.
    private async Task StartAsync(Game game)
    {
        _startedGame = game;
        await _detaching;
        if (_isDisposed || !ReferenceEquals(_startedGame, game))
        {
            return;
        }

        _reference ??= DotNetObjectReference.Create(this);
        var view = new GameViewConnection(JsRuntime, game);
        view.Delivered += OnDelivered;
        _view = view;
        try
        {
            await StartViewAsync(view, _reference);
        }
        catch (Exception exception) when (exception is OperationCanceledException or JSDisconnectedException or ObjectDisposedException)
        {
            // The page went away, or was shown for another game, while the game was starting.
        }
        catch (JSException exception)
        {
            if (ReferenceEquals(_view, view))
            {
                _error = $"The game could not be started: {exception.Message}";
                StateHasChanged();
            }
        }
    }

    private async Task StartViewAsync(GameViewConnection view, DotNetObjectReference<GamePage> reference)
    {
        var result = await view.StartAsync(_canvas, _pad, reference);
        if (!ReferenceEquals(_view, view))
        {
            // The disposal of the view has asked the module to drop it, which the start then releases.
            return;
        }

        if (!result.IsStarted)
        {
            _error = result.Error ?? "The game could not be started.";
            StateHasChanged();
            return;
        }

        var game = view.Game;
        _isStarted = true;
        if (_subscribedGame is { } formerGame)
        {
            formerGame.VolumeTableChanged -= OnVolumeTableChanged;
        }

        _subscribedGame = game;
        game.VolumeTableChanged += OnVolumeTableChanged;
        view.Open(CurrentConnection.Connection?.ClientId);
        var voice = new GameVoiceConnection(JsRuntime, Logger, game, view.ViewId, InvokeAsync);
        voice.Changed += StateHasChanged;
        _voice = voice;
        await voice.StartAsync(reference, _panel);
    }

    // Leaves the game without waiting for it or the browser: drops the page's share of the voice toggles, its speaker,
    // its view session and its handler, clears the panel and starts disposing the connections.
    private void Detach()
    {
        _startedGame = null;
        _layoutKey++;
        var view = _view;
        var voice = _voice;
        _view = null;
        _voice = null;
        voice?.StopInGame();
        view?.Close();
        if (view is not null || voice is not null)
        {
            _detaching = DisposeConnectionsAsync(_detaching, voice, view);
        }

        if (_subscribedGame is { } game)
        {
            game.VolumeTableChanged -= OnVolumeTableChanged;
            _subscribedGame = null;
        }

        _isStarted = false;
        _error = null;
        _clientId = null;
        _player = null;
        _positionText = GamePanelState.NoPosition;
        _nearby = [];
        _nearbyPlayer = null;
        _nearbyPlayerIds = [];
        _nearbyPublishVersion = -1;
        _targets = [];
        _actions = [];
    }

    // After the disposal of connections left before. The voice first, separately from the view, so a voice module that
    // the browser already dropped does not keep the game module. Never faults, since a start awaits it in
    // OnAfterRenderAsync, where an exception would end the circuit.
    private async Task DisposeConnectionsAsync(Task previous, IAsyncDisposable? voice, IAsyncDisposable? view)
    {
        await previous;
        await TryDisposeAsync(voice);
        await TryDisposeAsync(view);
    }

    private async Task TryDisposeAsync(IAsyncDisposable? connection)
    {
        try
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
        }
        catch (Exception exception)
        {
            Logger.LogDebug(exception, "Disposing a connection of the game page failed");
        }
    }

    // Raised on the thread pool, so it must not throw.
    private void OnDelivered() => _ = FlushDeliveredAsync();

    private async Task FlushDeliveredAsync()
    {
        try
        {
            await InvokeAsync(FlushAsync);
        }
        catch (Exception exception) when (exception is OperationCanceledException or JSDisconnectedException or ObjectDisposedException)
        {
            // The page went away.
        }
        catch (Exception exception)
        {
            Logger.LogDebug(exception, "Applying a view update failed");
        }
    }

    // Raised on the simulation thread inside a tick, so it only schedules a flush.
    private void OnVolumeTableChanged()
    {
        Volatile.Write(ref _isVolumeTableChanged, 1);
        ScheduleFlush();
    }

    // Called on the simulation thread inside a tick, so it never blocks.
    private void ScheduleFlush()
    {
        if (Interlocked.Exchange(ref _isFlushScheduled, 1) == 0)
        {
            _ = FlushLaterAsync();
        }
    }

    private async Task FlushLaterAsync()
    {
        try
        {
            await Task.Delay(FlushDelay, _lifetime.Token);
            Volatile.Write(ref _isFlushScheduled, 0);
            await InvokeAsync(FlushAsync);
        }
        catch (Exception exception) when (exception is OperationCanceledException or JSDisconnectedException or ObjectDisposedException)
        {
            // The page went away.
        }
    }

    // A flush requested while one runs makes the running one go again instead of overlapping it.
    private async Task FlushAsync()
    {
        if (_isFlushing)
        {
            _isFlushRequested = true;
            return;
        }

        _isFlushing = true;
        try
        {
            do
            {
                _isFlushRequested = false;
                await FlushOnceAsync();
            }
            while (_isFlushRequested && !_isDisposed);
        }
        finally
        {
            _isFlushing = false;
        }
    }

    // Applies the update the view session delivered, if any, and refreshes the panel.
    private async Task FlushOnceAsync()
    {
        if (_isDisposed || !_isStarted || _view is not { } view)
        {
            return;
        }

        var hasDelivery = await view.ApplyDeliveredAsync();
        if (_isDisposed || !_isStarted || !ReferenceEquals(_view, view))
        {
            return;
        }

        var isChanged = false;
        if (hasDelivery)
        {
            isChanged = _positionText != view.PanelState.PositionText;
            _positionText = view.PanelState.PositionText;
        }

        isChanged |= ResolvePlayer(view);
        isChanged |= await RefreshPanelAsync(view, hasDelivery);

        // Most deliveries change only the canvas, which the browser draws, so the panel renders only when it changed.
        if (isChanged && !_isDisposed)
        {
            StateHasChanged();
        }
    }

    // The player is the subject of the session's player, once a publish attached it to the game. Returns whether it
    // changed.
    private bool ResolvePlayer(GameViewConnection view)
    {
        _clientId = CurrentConnection.Connection?.ClientId;
        view.Follow(_clientId);
        var former = _player;
        _player = view.PanelState.PlayerClientId is { } clientId && clientId == _clientId && view.Game.Players.TryGetValue(clientId, out var player)
            ? player
            : null;
        if (_voice is { } voice)
        {
            voice.Player = _player;
        }

        return !ReferenceEquals(former, _player);
    }

    // Nearby follows the view, which carries the toggles, the publishes, which carry the titles, and the volume table.
    // Returns whether Nearby or the actions changed.
    private async Task<bool> RefreshPanelAsync(GameViewConnection view, bool hasDelivery)
    {
        var game = view.Game;
        var isVolumeTableChanged = Interlocked.Exchange(ref _isVolumeTableChanged, 0) == 1;
        var isPublished = game.PublishVersion != _nearbyPublishVersion;
        var isChanged = (hasDelivery || isVolumeTableChanged || isPublished) && RefreshNearby(game, hasDelivery || isPublished);
        isChanged |= RefreshActions(game, view.PanelState);
        if (isVolumeTableChanged && _voice is { } voice)
        {
            await voice.SynchronizeAsync();
        }

        return isChanged;
    }

    public async ValueTask DisposeAsync()
    {
        _isDisposed = true;
        Detach();
        try
        {
            await _lifetime.CancelAsync();
            await _detaching;
        }
        finally
        {
            _reference?.Dispose();
            _lifetime.Dispose();
        }
    }

    public sealed record StepResult(int Sequence, bool IsAccepted, int X, int Y, int Facing);

    private sealed record PadButton(string? Move, string Label, bool IsDiagonal);
}
