using Namotion.Interceptor.Tracking.Change;

namespace HomeBlaze.History;

/// <summary>
/// The instant from which a store's change subscription holds every change no session has consumed yet, so
/// that the next session's coverage may start there. Owned by a store subject, whose sequential session flow
/// is the only caller: <see cref="Track"/> when a processor is created and <see cref="EndSession"/> once a
/// session's processor has stopped consuming.
/// </summary>
public sealed class HistorySessionCoverage
{
    // Held weakly because it is only compared by identity, so one the service released is not pinned here
    // with its undrained queue.
    private WeakReference<PropertyChangeQueueSubscription>? _subscription;

    /// <summary>
    /// The instant the next session's coverage starts at.
    /// </summary>
    public DateTimeOffset StartsAt { get; private set; }

    /// <summary>
    /// Records the subscription the next session's processor consumes. A new subscription captures from now
    /// on; a kept one still holds everything since <see cref="EndSession"/> was last called, which the next
    /// session delivers collapsed to the newest value per property.
    /// </summary>
    public void Track(PropertyChangeQueueSubscription subscription)
    {
        if (_subscription is null || !_subscription.TryGetTarget(out var previous) || !ReferenceEquals(subscription, previous))
        {
            _subscription = new WeakReference<PropertyChangeQueueSubscription>(subscription);
            StartsAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Records that the session's processor has stopped consuming, so every later change waits in the
    /// subscription for the next session, and returns that instant, where the next session's coverage starts.
    /// </summary>
    /// <remarks>
    /// Call it before anything that waits, or the returned instant claims a window nothing was recording in.
    /// </remarks>
    public DateTimeOffset EndSession()
    {
        StartsAt = DateTimeOffset.UtcNow;
        return StartsAt;
    }
}
