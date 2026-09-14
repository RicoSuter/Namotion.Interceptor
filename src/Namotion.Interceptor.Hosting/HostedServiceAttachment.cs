using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Hosting;

/// <summary>
/// A hosted service bound to a subject. The handler creates the instance when the subject enters the
/// graph and disposes it when the subject leaves, so the same attachment yields a fresh instance on
/// every re-attach.
/// </summary>
public interface IHostedServiceAttachment
{
    /// <summary>The running instance, or null when nothing is running.</summary>
    IHostedService? Current { get; }

    /// <summary>The exception from the last failed transition, or null.</summary>
    Exception? Fault { get; }

    /// <summary>
    /// The state and the instance it was derived from, together, so the two cannot disagree.
    /// </summary>
    /// <remarks>
    /// Prefer this to reading <see cref="State"/> and <see cref="Current"/> separately: a transition
    /// can land between two reads, and a caller that then discards what the instance published acts on
    /// a pairing that never existed. Take the reading immediately before the decision it informs, since
    /// anything in between widens the window it exists to close.
    /// </remarks>
    HostedServiceAttachmentState GetState(out IHostedService? current);

    /// <summary>
    /// What the attachment is doing right now. Separates a start in flight, a stop in flight and a
    /// terminal attachment from a settled one with nothing running, which <see cref="Current"/> and
    /// <see cref="Fault"/> reading null cannot.
    /// </summary>
    HostedServiceAttachmentState State { get; }
}

/// <inheritdoc />
public interface IHostedServiceAttachment<T> : IHostedServiceAttachment
    where T : class, IHostedService
{
    /// <inheritdoc cref="IHostedServiceAttachment.Current" />
    new T? Current { get; }

    /// <summary>
    /// The state and the instance it was derived from, together, so the two cannot disagree.
    /// </summary>
    /// <remarks>
    /// Prefer this to reading <see cref="IHostedServiceAttachment.State"/> and <see cref="Current"/>
    /// separately: a transition can land between two reads, and a caller that then discards what the
    /// instance published acts on a pairing that never existed. Take the reading immediately before the
    /// decision it informs, since anything in between widens the window it exists to close.
    /// </remarks>
    HostedServiceAttachmentState GetState(out T? current);
}

/// <summary>
/// Lets the handler reach the target from a non generic attachment. An abstract base class cannot
/// serve here: the generic and non generic <c>Current</c> differ only by return type, so declaring
/// both on one class is CS0102. The non generic one is implemented explicitly instead.
/// </summary>
internal interface IHostedServiceAttachmentTarget
{
    HostedServiceTarget Target { get; }
}

internal sealed class HostedServiceAttachment<T> : IHostedServiceAttachment<T>, IHostedServiceAttachmentTarget
    where T : class, IHostedService
{
    public HostedServiceAttachment(HostedServiceTarget target)
    {
        Target = target;
    }

    public HostedServiceTarget Target { get; }

    public T? Current => (T?)Target.Current;

    public Exception? Fault => Target.Fault;

    public HostedServiceAttachmentState State => Target.State;

    public HostedServiceAttachmentState GetState(out T? current)
    {
        var state = Target.GetState(out var instance);
        current = (T?)instance;
        return state;
    }

    IHostedService? IHostedServiceAttachment.Current => Target.Current;

    HostedServiceAttachmentState IHostedServiceAttachment.GetState(out IHostedService? current)
        => Target.GetState(out current);
}
