using System.Runtime.CompilerServices;

namespace Namotion.Interceptor;

/// <summary>
/// One-shot, per-write origin handoff. <see cref="Set"/> stores a pending stamp for exactly
/// one write of one property; the matching write chain consumes it at
/// <c>PropertyWriteContext</c> construction. Nested writes (hooks, INPC handlers, derived
/// recalculations) never inherit it: the slot is either already consumed or targets a
/// different property. The scope captures the previous frame and restores it on dispose
/// (a zero-allocation stack through nested ref structs, like SubjectChangeContextScope),
/// so a cancelled write cannot leak the stamp and a nested stamped write cannot destroy
/// an outer stamp. Same-property re-entry from OnChanging is unsupported (the inner
/// invocation consumes the stamp). Thread-static by design: set and consume happen
/// synchronously within one call frame, never across await. Internal: producers use
/// intent-level APIs (SetValueFromSource, ApplySubjectUpdate, transaction replay).
/// </summary>
internal static class PendingOrigin
{
    [ThreadStatic] private static bool _armed;
    [ThreadStatic] private static PropertyReference _target;
    [ThreadStatic] private static ChangeOrigin _origin;
    [ThreadStatic] private static object? _sentValue;

    internal static PendingOriginScope Set(PropertyReference target, ChangeOrigin origin, object? sentValue)
    {
        var scope = new PendingOriginScope(_armed, _target, _origin, _sentValue);
        _armed = true;
        _target = target;
        _origin = origin;
        _sentValue = sentValue;
        return scope;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryConsume(in PropertyReference property, out ChangeOrigin origin, out object? sentValue)
    {
        if (_armed && _target.Equals(property))
        {
            origin = _origin;
            sentValue = _sentValue;
            _armed = false;
            _target = default;
            _origin = default;
            _sentValue = null;
            return true;
        }

        origin = ChangeOrigin.Local;
        sentValue = null;
        return false;
    }

    internal static void Restore(bool armed, in PropertyReference target, ChangeOrigin origin, object? sentValue)
    {
        _armed = armed;
        _target = target;
        _origin = origin;
        _sentValue = sentValue;
    }
}

internal readonly ref struct PendingOriginScope
{
    private readonly bool _previousArmed;
    private readonly PropertyReference _previousTarget;
    private readonly ChangeOrigin _previousOrigin;
    private readonly object? _previousSentValue;

    internal PendingOriginScope(bool armed, PropertyReference target, ChangeOrigin origin, object? sentValue)
    {
        _previousArmed = armed;
        _previousTarget = target;
        _previousOrigin = origin;
        _previousSentValue = sentValue;
    }

    public void Dispose() => PendingOrigin.Restore(_previousArmed, in _previousTarget, _previousOrigin, _previousSentValue);
}
