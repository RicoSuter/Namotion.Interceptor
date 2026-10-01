namespace Namotion.Interceptor;

/// <summary>
/// Records what one write did, so a caller that drives a property setter indirectly through
/// <see cref="SubjectPropertyMetadata.SetValue"/>, which returns void, can tell a vetoed write from an
/// applied one. A class rather than a struct because the facts recorded before a hook or observer throws
/// must survive the unwind back to the caller that armed it, which an out parameter cannot do. An instance
/// is not thread safe and must not be shared across threads: <see cref="Arm"/> resets both flags, so a
/// second thread arming the same instance silently erases the first thread's result.
/// </summary>
public sealed class PropertyWriteOutcome
{
    /// <summary>
    /// Gets whether the write reached assignment, or stopped short of it because its value, after the
    /// subject's own changing hook ran, already equalled the current one, which is a successful no-op.
    /// Derived from the values the write started with, so a write that was refused at a value already
    /// equal to the current one is indistinguishable from an accepted no-op; nothing was mutated in
    /// either case, so there is nothing to compensate. A write of a differing value that was cancelled,
    /// vetoed or suppressed is not accepted.
    /// </summary>
    public bool Accepted { get; internal set; }

    /// <summary>
    /// Gets whether the terminal assignment completed, even if a later callback threw over it.
    /// </summary>
    public bool Mutated { get; internal set; }

    /// <summary>
    /// Arms this outcome for the next write of <paramref name="property"/> on this thread and clears the
    /// previous result. Dispose the scope once that write has returned or thrown. A write of any other
    /// property leaves this outcome untouched, including one nested inside the armed write. A nested
    /// re-entrant write of the armed property itself consumes the arming and reports here instead. From
    /// its changing hook that is a full report; from a derived recalculation cascading into it, only a
    /// mutation is reported, never an accepted no-op, so a recalculation to an equal value leaves both
    /// flags clear.
    /// </summary>
    public PropertyWriteOutcomeScope Arm(PropertyReference property, ChangeOrigin origin, object? sentValue)
    {
        Accepted = false;
        Mutated = false;
        return new PropertyWriteOutcomeScope(PendingOrigin.Arm(property, origin, sentValue, this));
    }
}

/// <summary>
/// Restores the previous pending write frame on disposal, which bounds the arming to the scope's extent:
/// within the scope, the next write of the armed property is the one observed, even after an earlier
/// write of it was cancelled before reaching the chain; after disposal no later write on the thread can
/// claim the outcome.
/// </summary>
public readonly ref struct PropertyWriteOutcomeScope
{
    private readonly PendingOriginScope _scope;

    // A public ref struct is always default-constructible, and restoring a default frame would disarm
    // whatever the thread has armed, so only a scope the library constructed restores on disposal.
    private readonly bool _isArmed;

    internal PropertyWriteOutcomeScope(PendingOriginScope scope)
    {
        _scope = scope;
        _isArmed = true;
    }

    /// <summary>
    /// Restores the pending write frame that was current before <see cref="PropertyWriteOutcome.Arm"/>,
    /// which ends the arming. A default instance restores nothing.
    /// </summary>
    public void Dispose()
    {
        if (_isArmed)
        {
            _scope.Dispose();
        }
    }
}
