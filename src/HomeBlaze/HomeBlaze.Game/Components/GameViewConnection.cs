using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.View;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HomeBlaze.Game.Components;

/// <summary>
/// The page's side of its view session: starts the view in the browser, applies the updates the session delivers there,
/// one at a time, and forwards the steps of the page's player. Runs on the renderer, except the members of
/// <see cref="IGameViewSink"/>, which the simulation thread calls.
/// </summary>
internal sealed class GameViewConnection : IGameViewSink, IThreadPoolWorkItem, IAsyncDisposable
{
    private const string ModulePath = "./_content/HomeBlaze.Game/game.js";
    private const string BaseUrl = "./_content/HomeBlaze.Game/";

    private static int _lastViewId;

    private readonly IJSRuntime _jsRuntime;
    private readonly CancellationTokenSource _lifetime = new();
    // Read from the token source once, so calls that run after the disposal see it canceled rather than disposed.
    private readonly CancellationToken _lifetimeToken;
    private IJSObjectReference? _module;
    private GameViewSession? _session;
    // The client the view session follows.
    private string? _clientId;
    private ViewDelivery? _delivery;
    // Set from a delivery until the flush that applied it released it, so at most one update is on its way to the
    // browser and the session collects the changes meanwhile.
    private int _isBusy;
    private int _isClosed;
    private bool _isBroken;
    private bool _isDisposed;

    public GameViewConnection(IJSRuntime jsRuntime, Game game)
    {
        _jsRuntime = jsRuntime;
        Game = game;
        _lifetimeToken = _lifetime.Token;
    }

    /// <summary>
    /// Identifies the page's view in the module, which the pages of a tab share, and the page's voice.
    /// </summary>
    public int ViewId { get; } = Interlocked.Increment(ref _lastViewId);

    /// <summary>
    /// The game the view shows.
    /// </summary>
    public Game Game { get; }

    /// <summary>
    /// The panel state of the last update the page took from its view session.
    /// </summary>
    public GamePanelState PanelState { get; private set; } = GamePanelState.Empty;

    /// <summary>
    /// Raised on the thread pool after the session delivered an update, which <see cref="ApplyDeliveredAsync"/> takes. A
    /// handler must not throw, which would end the process.
    /// </summary>
    public event Action? PanelChanged;

    /// <inheritdoc />
    public bool IsBusy => Volatile.Read(ref _isBusy) == 1 || Volatile.Read(ref _isClosed) == 1;

    /// <summary>
    /// Starts the view in the browser; a disposal meanwhile makes the module release the view.
    /// </summary>
    public async Task<StartResult> StartAsync(ElementReference canvas, ElementReference pad, object dotNetReference)
    {
        var module = await _jsRuntime.InvokeAsync<IJSObjectReference>("import", _lifetimeToken, ModulePath);
        if (_isDisposed)
        {
            // DisposeAsync found no module to release.
            await module.DisposeAsync();
            return new StartResult(false, null);
        }

        _module = module;
        var options = new { viewId = ViewId, pad, stepIntervalMs = (int)Game.StepInterval.TotalMilliseconds, baseUrl = BaseUrl };
        return await module.InvokeAsync<StartResult>("start", _lifetimeToken, canvas, dotNetReference, options);
    }

    /// <summary>
    /// Opens the view session, which delivers its first update, everything in view, in the next tick of the game, also one
    /// after a restart.
    /// </summary>
    public void Open(string? clientId)
    {
        _clientId = clientId;
        _session = Game.Views.Open(clientId, this);
    }

    /// <summary>
    /// Makes the view session follow the player of the client.
    /// </summary>
    public void Follow(string? clientId)
    {
        if (_session is { } session && clientId != _clientId)
        {
            _clientId = clientId;
            session.ClientId = clientId;
        }
    }

    // On the simulation thread, so the flush runs on the thread pool: the renderer may run work inline on the calling
    // thread when it is idle. The work item carries no state, so an instance that is still queued may be queued again.
    /// <inheritdoc />
    public void Deliver(GameViewUpdate update, GamePanelState panel)
    {
        Volatile.Write(ref _isBusy, 1);
        Volatile.Write(ref _delivery, new ViewDelivery(update, panel));
        ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
    }

    void IThreadPoolWorkItem.Execute() => PanelChanged?.Invoke();

    /// <summary>
    /// Applies the update the session delivered, if any, in the browser and releases the session for the next one; returns
    /// whether there was one, whose panel state <see cref="PanelState"/> then holds. Runs one call at a time.
    /// </summary>
    public async Task<bool> ApplyDeliveredAsync()
    {
        if (Interlocked.Exchange(ref _delivery, null) is not { } delivery)
        {
            return false;
        }

        // Taken first, so an update the browser refused does not drop the panel. The panel keeps following the game
        // after the view broke, so the session is released for every update all the same.
        PanelState = delivery.Panel;
        try
        {
            if (!_isBroken && !delivery.Update.IsEmpty && _module is { } module)
            {
                await TryApplyUpdateAsync(module, delivery.Update);
            }
        }
        finally
        {
            // The session waits for this before it delivers the next update.
            Volatile.Write(ref _isBusy, 0);
        }

        return true;
    }

    /// <summary>
    /// Moves the player of the client; returns null when the game did not answer, has stopped, or the connection was
    /// disposed, which the browser treats as an unanswered step.
    /// </summary>
    public async Task<GamePage.StepResult?> StepAsync(string clientId, int sequence, int direction, bool turnOnly)
    {
        try
        {
            return await Game.StepAsync(clientId, (GameDirection)direction, turnOnly, _lifetimeToken) is { } result
                ? new GamePage.StepResult(sequence, result.IsAccepted, result.X, result.Y, (int)result.Facing)
                : null;
        }
        catch (Exception exception) when (exception is GameStoppedException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Ends the view session in the game, which then delivers nothing more; does not wait for the game or the browser.
    /// </summary>
    public void Close()
    {
        Volatile.Write(ref _isClosed, 1);
        _session?.Dispose();
        _session = null;
    }

    /// <summary>
    /// Closes the view session and drops the view in the browser.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Close();
        try
        {
            await _lifetime.CancelAsync();
            if (_module is { } module)
            {
                await module.InvokeVoidAsync("dispose", ViewId);
                await module.DisposeAsync();
            }
        }
        catch (Exception exception) when (exception is JSDisconnectedException or JSException or OperationCanceledException)
        {
            // The circuit is gone, and the browser with it, the browser already dropped the module, or did not answer.
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    private async Task TryApplyUpdateAsync(IJSObjectReference module, GameViewUpdate update)
    {
        try
        {
            await module.InvokeVoidAsync("applyUpdate", _lifetimeToken, ViewId, update);
        }
        catch (Exception exception) when (exception is OperationCanceledException or JSDisconnectedException)
        {
            // The page went away.
        }
        catch (JSException)
        {
            // The module dropped the view or broke, so every later update would fail too.
            _isBroken = true;
        }
    }

    public sealed record StartResult(bool IsStarted, string? Error);

    private sealed record ViewDelivery(GameViewUpdate Update, GamePanelState Panel);
}
