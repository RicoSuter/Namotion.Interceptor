using System.Runtime.CompilerServices;

namespace Namotion.Interceptor;

/// <summary>
/// One-shot, per-write handoff between a caller and the write chain it is about to drive, in both
/// directions: an origin stamp travels in, and an optional <see cref="PropertyWriteOutcome"/> that
/// the write reports into travels out. An origin moves through three stages: pending (set in the
/// slot, waiting for its write), then attempted (consumed into the write context, carried
/// unverified), then finalized (verified or demoted to Local at the terminal write).
/// <see cref="Arm"/> (and <see cref="Set"/>, its outcome-free form) stores a frame for exactly
/// one write of one property; the matching write chain consumes the whole frame at
/// <c>PropertyWriteContext</c> construction, so the outcome rides the lookup the origin already
/// pays for. A nested write of a different property (a hook, an INPC handler, a derived
/// recalculation) never consumes it, because the frame targets one property; a nested re-entry on
/// the armed property does, whether from OnChanging or from a derived recalculation cascading into
/// it, so the outcome then describes that inner write rather than the one the caller issued. The
/// cascade entry point has no equality-acceptance step, so an outcome it consumes reports a mutation
/// but never an accepted no-op: a recalculation to an equal value leaves both flags clear. The
/// scope captures the previous frame and restores it on dispose (a zero-allocation stack through
/// nested ref structs, like SubjectChangeContextScope), which bounds the arming to the scope's
/// extent: within it, the next write of the target property is the one observed, even after an
/// earlier write of it was cancelled before reaching the chain; after disposal a cancelled write
/// has leaked neither the stamp nor the outcome, and a nested armed write cannot destroy an outer
/// frame. Same-property re-entry from OnChanging is unsupported for that reason (the inner
/// invocation consumes the frame).
/// Thread-static by design: arm and consume happen synchronously within one call frame,
/// never across await. Internal: producers use intent-level APIs (SetValueFromSource,
/// ApplySubjectUpdate, transaction replay) and <see cref="PropertyWriteOutcome.Arm"/>.
/// </summary>
internal static class PendingOrigin
{
    /// <summary>
    /// The pending frame held in a single thread-static slot so Arm/TryConsume/Restore perform one
    /// TLS lookup plus field offsets instead of separate slot accesses, and each reset is a
    /// single default assignment (so no reference can be left behind partially).
    /// </summary>
    internal struct PendingFrame
    {
        public bool HasValue;
        public PropertyReference Target;
        public AttemptedOrigin Attempted;
        public PropertyWriteOutcome? Outcome;
    }

    [ThreadStatic] private static PendingFrame _frame;

    internal static PendingOriginScope Set(PropertyReference target, ChangeOrigin origin, object? sentValue)
        => Arm(target, origin, sentValue, outcome: null);

    /// <summary>
    /// Arms the frame for one write, optionally with an outcome the write reports into. Rides the same
    /// slot as the origin stamp so an ordinary write pays no second thread-static lookup: the write
    /// context already consumes this frame on construction.
    /// </summary>
    internal static PendingOriginScope Arm(PropertyReference target, ChangeOrigin origin, object? sentValue, PropertyWriteOutcome? outcome)
    {
        var scope = new PendingOriginScope(_frame);
        _frame = new PendingFrame
        {
            HasValue = true,
            Target = target,
            Attempted = new AttemptedOrigin(origin, sentValue),
            Outcome = outcome
        };
        return scope;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryConsume(in PropertyReference property, out AttemptedOrigin attempted)
        => TryConsume(in property, out attempted, out _);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryConsume(in PropertyReference property, out AttemptedOrigin attempted, out PropertyWriteOutcome? outcome)
    {
        if (_frame.HasValue && _frame.Target.Equals(property))
        {
            attempted = _frame.Attempted;
            outcome = _frame.Outcome;
            _frame = default;
            return true;
        }

        attempted = default;
        outcome = null;
        return false;
    }

    internal static void Restore(in PendingFrame frame)
    {
        _frame = frame;
    }
}

internal readonly ref struct PendingOriginScope
{
    private readonly PendingOrigin.PendingFrame _previousFrame;

    internal PendingOriginScope(in PendingOrigin.PendingFrame previousFrame)
    {
        _previousFrame = previousFrame;
    }

    public void Dispose() => PendingOrigin.Restore(in _previousFrame);
}
