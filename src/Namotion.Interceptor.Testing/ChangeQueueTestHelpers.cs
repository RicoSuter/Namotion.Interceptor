using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Testing;

/// <summary>
/// Helper methods for draining property change queue subscriptions in tests. Tests publish a
/// sentinel change on a fresh subject after the work under test, then drain up to it, so the
/// drain has a guaranteed end marker instead of a fixed delay.
/// </summary>
public static class ChangeQueueTestHelpers
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Drains the subscription until a change matches <paramref name="isSentinel"/> (excluded from
    /// the result), returning everything dequeued before it.
    /// </summary>
    /// <exception cref="TimeoutException">Thrown when no sentinel arrives within 10 seconds.</exception>
    public static List<SubjectPropertyChange> DrainUntil(
        PropertyChangeQueueSubscription subscription, Func<SubjectPropertyChange, bool> isSentinel)
    {
        var changes = new List<SubjectPropertyChange>();
        using var timeout = new CancellationTokenSource(DrainTimeout);
        while (subscription.TryDequeue(out var change, timeout.Token))
        {
            if (isSentinel(change))
            {
                return changes;
            }

            changes.Add(change);
        }

        throw new TimeoutException("Sentinel notification was not received within 10 seconds.");
    }

    /// <summary>
    /// Drains the subscription until the first change for <paramref name="sentinelSubject"/>
    /// arrives (excluded from the result), returning everything dequeued before it. The caller
    /// writes any property of the sentinel subject after the work under test.
    /// </summary>
    /// <exception cref="TimeoutException">Thrown when no sentinel arrives within 10 seconds.</exception>
    public static List<SubjectPropertyChange> DrainUntilSubject(
        PropertyChangeQueueSubscription subscription, IInterceptorSubject sentinelSubject) =>
        DrainUntil(subscription, change => ReferenceEquals(change.Property.Subject, sentinelSubject));
}
