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
    /// What the attachment is doing right now, together with the instance that reading was derived from,
    /// so the two cannot disagree. Separates a start in flight, a stop in flight and a terminal
    /// attachment from a settled one with nothing running, which <see cref="Current"/> and
    /// <see cref="Fault"/> reading null cannot.
    /// </summary>
    /// <remarks>
    /// The pair comes out of one load, which is why the state is not also exposed on its own: two reads
    /// leave a transition free to land between them. When and in which order to read before acting is
    /// in docs/hosting.md#reading-the-outcome.
    /// </remarks>
    HostedServiceAttachmentState GetState(out IHostedService? current);
}

/// <inheritdoc />
public interface IHostedServiceAttachment<T> : IHostedServiceAttachment
    where T : class, IHostedService
{
    /// <inheritdoc cref="IHostedServiceAttachment.Current" />
    new T? Current { get; }

    /// <inheritdoc cref="IHostedServiceAttachment.GetState" />
    HostedServiceAttachmentState GetState(out T? current);
}

/// <summary>
/// Lets the handler reach the slot from a non generic attachment. An abstract base class cannot
/// serve here: the generic and non generic <c>Current</c> differ only by return type, so declaring
/// both on one class is CS0102. The non generic one is implemented explicitly instead.
/// </summary>
internal interface IHostedServiceSlotAccess
{
    HostedServiceSlot Slot { get; }
}

internal sealed class HostedServiceAttachment<T> : IHostedServiceAttachment<T>, IHostedServiceSlotAccess
    where T : class, IHostedService
{
    public HostedServiceAttachment(HostedServiceSlot slot)
    {
        Slot = slot;
    }

    public HostedServiceSlot Slot { get; }

    public T? Current => (T?)Slot.Current;

    public Exception? Fault => Slot.Fault;

    public HostedServiceAttachmentState GetState(out T? current)
    {
        var state = Slot.GetState(out var instance);
        current = (T?)instance;
        return state;
    }

    IHostedService? IHostedServiceAttachment.Current => Slot.Current;

    HostedServiceAttachmentState IHostedServiceAttachment.GetState(out IHostedService? current)
        => Slot.GetState(out current);
}
