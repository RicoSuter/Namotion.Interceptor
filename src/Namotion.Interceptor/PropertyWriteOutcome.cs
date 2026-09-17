namespace Namotion.Interceptor;

/// <summary>
/// Records what one write did, so a caller that drives a property setter indirectly through
/// <see cref="SubjectPropertyMetadata.SetValue"/>, which returns void, can tell a vetoed write from an
/// applied one. A class rather than a struct because the facts recorded before a hook or observer throws
/// must survive the unwind back to the caller that armed it, which an out parameter cannot do.
/// </summary>
public sealed class PropertyWriteOutcome
{
    /// <summary>
    /// Gets whether the write reached assignment, or stopped because its final value already equalled
    /// the current one. A write that was cancelled, vetoed or suppressed is not accepted.
    /// </summary>
    public bool Accepted { get; internal set; }

    /// <summary>
    /// Gets whether the terminal assignment completed, even if a later callback threw over it.
    /// </summary>
    public bool Mutated { get; internal set; }

    /// <summary>
    /// Arms this outcome for the next write of <paramref name="property"/> on this thread and clears the
    /// previous result. Dispose the scope once that write has returned or thrown. A write of any other
    /// property, and any write nested inside the armed one, leaves this outcome untouched.
    /// </summary>
    public PropertyWriteOutcomeScope Arm(PropertyReference property, ChangeOrigin origin, object? sentValue)
    {
        Accepted = false;
        Mutated = false;
        return new PropertyWriteOutcomeScope(PendingOrigin.Arm(property, origin, sentValue, this));
    }
}

/// <summary>
/// Restores the previous pending write frame on disposal, so a write that never happened cannot leave an
/// outcome armed for an unrelated later write on the same thread.
/// </summary>
public readonly ref struct PropertyWriteOutcomeScope : IDisposable
{
    private readonly PendingOriginScope _scope;

    internal PropertyWriteOutcomeScope(PendingOriginScope scope)
    {
        _scope = scope;
    }

    /// <inheritdoc />
    public void Dispose() => _scope.Dispose();
}
