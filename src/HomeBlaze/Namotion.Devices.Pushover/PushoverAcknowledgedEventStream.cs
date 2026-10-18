using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;

namespace Namotion.Devices.Pushover;

/// <summary>
/// The acknowledgement events of the emergencies of a <see cref="PushoverService"/>, for all targets or one key.
/// </summary>
internal sealed class PushoverAcknowledgedEventStream
{
    private readonly ILogger _logger;

    // Synchronized, since the receipt polling and a cancel can raise events at the same time.
    private readonly ISubject<PushoverEmergencyAcknowledgedEvent> _events =
        Subject.Synchronize(new Subject<PushoverEmergencyAcknowledgedEvent>());

    public PushoverAcknowledgedEventStream(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Emits the events of the target with <paramref name="targetKey"/>, or of every target when null.
    /// </summary>
    public IDisposable Subscribe(IObserver<PushoverEmergencyAcknowledgedEvent> observer, string? targetKey)
    {
        ArgumentNullException.ThrowIfNull(observer);

        // Each subscriber is wrapped so that one throwing from OnNext cannot stop the subject from notifying the
        // others, or propagate into the receipt polling or the cancel that raises the event.
        return _events.Subscribe(
            value =>
            {
                if (targetKey is not null && value.TargetKey != targetKey)
                {
                    return;
                }

                try
                {
                    observer.OnNext(value);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "A Pushover emergency acknowledgement subscriber of target {Target} threw", value.TargetKey);
                }
            },
            observer.OnError,
            observer.OnCompleted);
    }

    public void Publish(PushoverEmergencyAcknowledgedEvent acknowledgedEvent) => _events.OnNext(acknowledgedEvent);
}
