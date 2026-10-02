using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// A service whose stop parks until the test releases it, ignoring its token, and which can attach a
/// child service to its subject from its start, so a test can hold the service mid stop and look at
/// the child.
/// </summary>
public sealed class GatedStopService : IHostedService, IDisposable
{
    private readonly IInterceptorSubject? _subject;
    private int _disposeCount;
    private int _stopReturned;
    private int _childStopOrder;

    /// <param name="subject">The subject to attach a child to on start, or null for no child.</param>
    public GatedStopService(IInterceptorSubject? subject = null)
    {
        _subject = subject;
    }

    public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource StopRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ScriptedBackgroundService? Child { get; private set; }

    public IHostedServiceAttachment? ChildAttachment { get; private set; }

    public bool IsDisposed => Volatile.Read(ref _disposeCount) > 0;

    /// <summary>
    /// Null until the child's stop has begun, then whether this service's stop had already returned
    /// by then.
    /// </summary>
    public bool? ChildStopBeganAfterThisStopReturned => Volatile.Read(ref _childStopOrder) switch
    {
        0 => null,
        1 => false,
        _ => true
    };

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_subject is not null)
        {
            ChildAttachment = _subject.AttachHostedService(() =>
                Child = new ScriptedBackgroundService(token =>
                {
                    // The child's stop cancels this token first thing, so the callback marks its start.
                    token.Register(() => Volatile.Write(ref _childStopOrder, Volatile.Read(ref _stopReturned) + 1));
                    return Task.Delay(Timeout.Infinite, token);
                }));
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        StopEntered.TrySetResult();
        await StopRelease.Task;
        Volatile.Write(ref _stopReturned, 1);
    }

    public void Dispose() => Interlocked.Increment(ref _disposeCount);
}
