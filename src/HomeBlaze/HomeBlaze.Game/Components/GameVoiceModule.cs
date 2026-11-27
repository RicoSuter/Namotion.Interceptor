using HomeBlaze.Game.Voice;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace HomeBlaze.Game.Components;

/// <summary>
/// The voice module in the browser, for the voice of one view. A call that fails is logged and only leaves the voice off;
/// after the disposal, which stops everything in the browser, the calls do nothing. Runs on the renderer, except
/// <see cref="Play"/>.
/// </summary>
internal sealed class GameVoiceModule : IAsyncDisposable
{
    private const string ModulePath = "./_content/HomeBlaze.Game/game-voice.js";

    private readonly IJSObjectReference _module;
    private readonly int _viewId;
    private readonly ILogger _logger;
    private readonly Func<Func<Task>, Task> _invokeAsync;
    private readonly CancellationToken _cancellationToken;
    private int _pendingSends;
    private bool _isDisposed;

    private GameVoiceModule(IJSObjectReference module, int viewId, ILogger logger, Func<Func<Task>, Task> invokeAsync,
        CancellationToken cancellationToken)
    {
        _module = module;
        _viewId = viewId;
        _logger = logger;
        _invokeAsync = invokeAsync;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Loads the voice module.
    /// </summary>
    /// <param name="jsRuntime">The JS runtime of the page.</param>
    /// <param name="viewId">The id of the page's view, which also identifies its voice.</param>
    /// <param name="logger">Logs failed calls.</param>
    /// <param name="invokeAsync">Runs work on the page's renderer.</param>
    /// <param name="cancellationToken">Cancels the calls once the page's voice ends.</param>
    public static async Task<GameVoiceModule> ImportAsync(IJSRuntime jsRuntime, int viewId, ILogger logger,
        Func<Func<Task>, Task> invokeAsync, CancellationToken cancellationToken)
    {
        var module = await jsRuntime.InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath);
        return new GameVoiceModule(module, viewId, logger, invokeAsync, cancellationToken);
    }

    /// <summary>
    /// Prepares the voice of the view; returns false where the browser offers no voice. Clicks on the voice buttons inside
    /// the panel unlock the audio.
    /// </summary>
    public ValueTask<bool> InitializeAsync(object dotNetReference, ElementReference panel) =>
        _module.InvokeAsync<bool>("initialize", _cancellationToken, _viewId, dotNetReference, CreateOptions(panel));

    /// <summary>
    /// Starts capture or playback; a result without an error means the browser did not answer in time, and the function
    /// with the stop identifier is then invoked, since the start may still succeed there later.
    /// </summary>
    public async Task<VoiceStartResult> TryStartAsync(string identifier, string stopIdentifier, TimeSpan timeout)
    {
        var result = await InvokeStartAsync(identifier, timeout);
        if (!result.IsStarted && result.Error is null)
        {
            await TryInvokeAsync(stopIdentifier, GameVoiceSettings.BrowserCallTimeout);
        }

        return result;
    }

    /// <summary>
    /// Invokes a function of the module for the view.
    /// </summary>
    public async Task TryInvokeAsync(string identifier, TimeSpan timeout)
    {
        if (_isDisposed)
        {
            return;
        }

        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken);
            cancellation.CancelAfter(timeout);
            await _module.InvokeVoidAsync(identifier, cancellation.Token, _viewId);
        }
        catch (JSException exception)
        {
            _logger.LogDebug(exception, "The voice call {Identifier} failed", identifier);
        }
        catch (Exception exception) when (exception is OperationCanceledException or JSDisconnectedException or ObjectDisposedException)
        {
            // The page went away or the browser did not answer in time.
        }
    }

    private async Task<VoiceStartResult> InvokeStartAsync(string identifier, TimeSpan timeout)
    {
        if (_isDisposed)
        {
            return new VoiceStartResult(false, null);
        }

        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken);
            cancellation.CancelAfter(timeout);
            return await _module.InvokeAsync<VoiceStartResult>(identifier, cancellation.Token, _viewId);
        }
        catch (JSException exception)
        {
            _logger.LogDebug(exception, "The voice call {Identifier} failed", identifier);
        }
        catch (Exception exception) when (exception is OperationCanceledException or JSDisconnectedException or ObjectDisposedException)
        {
            // The page went away or the browser did not answer in time.
        }

        return new VoiceStartResult(false, null);
    }

    /// <summary>
    /// Hands a mixed frame to the renderer, which plays it in the browser; drops it while too many sends to the browser are
    /// pending. Called on the voice loop.
    /// </summary>
    public void Play(byte[] frame)
    {
        if (_isDisposed)
        {
            return;
        }

        if (Interlocked.Increment(ref _pendingSends) > GameVoiceSettings.MaximumPendingSends)
        {
            Interlocked.Decrement(ref _pendingSends);
            return;
        }

        _ = PlayAsync(frame);
    }

    /// <summary>
    /// Stops capture and playback in the browser and releases the module.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        try
        {
            await _module.InvokeVoidAsync("dispose", _viewId);
            await _module.DisposeAsync();
        }
        catch (Exception exception) when (exception is JSDisconnectedException or OperationCanceledException)
        {
            // The circuit is gone, and the browser with it, or the browser did not answer.
        }
        catch (JSException exception)
        {
            _logger.LogDebug(exception, "Disposing the voice of the game failed");
        }
    }

    private async Task PlayAsync(byte[] frame)
    {
        try
        {
            // JS interop with byte arrays runs on the circuit's dispatcher, not on the voice loop. The timeout releases
            // the pending send of a call that a lost connection never answers.
            await _invokeAsync(() => _module.InvokeVoidAsync("play", GameVoiceSettings.PlayTimeout, _viewId, frame).AsTask());
        }
        catch (Exception exception) when (exception is OperationCanceledException or JSDisconnectedException or ObjectDisposedException
            || (exception is JSException && _isDisposed))
        {
            // The page went away, or the frame was lost.
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Playing a voice frame failed");
        }
        finally
        {
            Interlocked.Decrement(ref _pendingSends);
        }
    }

    private static object CreateOptions(ElementReference panel) => new
    {
        element = panel,
        sampleRate = GameVoiceSettings.SampleRate,
        frameSamples = GameVoiceSettings.SamplesPerFrame,
        bufferStartSamples = ToSamples(GameVoiceSettings.PlaybackBufferStart),
        bufferMaximumSamples = ToSamples(GameVoiceSettings.PlaybackBufferMaximum),
        bufferGrowthSamples = GameVoiceSettings.BufferGrowth * GameVoiceSettings.SamplesPerFrame,
        bufferHeadroomSamples = GameVoiceSettings.TrimHeadroom * GameVoiceSettings.SamplesPerFrame,
        bufferShrinkDelaySeconds = GameVoiceSettings.BufferShrinkDelay.TotalSeconds,
        trimWindowSeconds = GameVoiceSettings.TrimWindow.TotalSeconds,
        rampSeconds = GameVoiceSettings.PlaybackRamp.TotalSeconds,
    };

    private static int ToSamples(TimeSpan duration) => (int)Math.Round(duration.TotalSeconds * GameVoiceSettings.SampleRate);

    public sealed record VoiceStartResult(bool IsStarted, string? Error);
}
