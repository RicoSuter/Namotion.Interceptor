namespace Namotion.Interceptor.Hosting;

/// <summary>
/// What an attachment is doing right now, for a consumer that polls the handle.
/// <see cref="IHostedServiceAttachment.Current"/> and <see cref="IHostedServiceAttachment.Fault"/> read
/// null before anything has started, while a start is in flight, while a stop is, and after a start the
/// handler declined. This separates a start in flight, a stop in flight and a terminal attachment from
/// the rest. It does not separate why a start was declined, and a declined start has no state of its
/// own: every refusal returns before the start clears the fault and before it enters its window, so the
/// attachment goes on reading exactly what it read before, whatever that was. A target already holding
/// an instance is one of the things a start is refused for, so a refusal can leave it at
/// <see cref="Running"/> as readily as at <see cref="Stopped"/>.
/// </summary>
public enum HostedServiceAttachmentState
{
    /// <summary>
    /// Nothing is running and no fault or detach is recorded: before the first start, after a stop that
    /// neither failed nor followed a detach, and after a declined start on a target that carries no
    /// fault. It does not mean nothing is queued. A start parked on a startup scope has been appended
    /// and is waiting inside its own body, and it reads here for the whole of that deferral, because a
    /// start enters its window only once it is past every guard and that wait. It says nothing about
    /// whether a further start would be accepted either, because a handler that is shutting down
    /// declines every one of them and leaves the attachment reading here.
    /// </summary>
    Stopped = 0,

    /// <summary>
    /// The handler is creating and starting an instance. The factory may already have published
    /// observable state, so a consumer must not treat what it sees as stale here.
    /// </summary>
    Starting = 1,

    /// <summary>An instance is running.</summary>
    Running = 2,

    /// <summary>The instance has left <see cref="IHostedServiceAttachment.Current"/> and is still stopping or being disposed.</summary>
    Stopping = 3,

    /// <summary>
    /// The attachment was detached explicitly, or its awaited attach faulted. No start appended after
    /// that point is accepted, so nothing can bring this attachment back. A start appended before it
    /// still runs, and the stop the detach appends behind that start stops and disposes what it
    /// created, so <see cref="Starting"/>, <see cref="Running"/> and <see cref="Stopping"/> can still
    /// follow before it settles back here. Named for the removal rather than for the detach: a context
    /// detach also detaches, and leaves the attachment restartable at <see cref="Stopped"/>.
    /// </summary>
    Removed = 4,

    /// <summary>The last attempt failed. The next one may still succeed.</summary>
    Faulted = 5
}
