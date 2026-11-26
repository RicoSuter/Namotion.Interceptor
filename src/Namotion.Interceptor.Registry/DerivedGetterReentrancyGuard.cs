using System.Diagnostics;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Tracking.Derived;

namespace Namotion.Interceptor.Registry;

/// <summary>
/// The getter of a dynamic derived property that gives the default of the property's type when it is evaluated again
/// on the same thread within the same derived evaluation, as a getter that reads itself through other properties would
/// otherwise recurse until the stack overflows. A recalculation nested in its own evaluation, such as one that a write
/// of the getter requests, runs the getter. Reports each property once through <see cref="Trace"/>.
/// </summary>
/// <remarks>An instance belongs to the property of one subject, so its getter is only called with that subject.</remarks>
internal sealed class DerivedGetterReentrancyGuard(string name, Type type, Func<IInterceptorSubject, object?> getValue)
{
    // The getters that this thread evaluates with the derived evaluation depth they started at, innermost last.
    [ThreadStatic]
    private static List<(DerivedGetterReentrancyGuard Guard, int Depth)>? _active;

    private int _hasReported;

    public object? GetValue(IInterceptorSubject subject)
    {
        var active = _active ??= [];
        var depth = DerivedEvaluation.Depth;
        if (active.Contains((this, depth)))
        {
            Report(subject);
            return CreateDefault();
        }

        active.Add((this, depth));
        try
        {
            return getValue(subject);
        }
        finally
        {
            active.RemoveAt(active.Count - 1);
        }
    }

    // Boxed, so that a property of a non-nullable value type reads a value of its type.
    private object? CreateDefault()
    {
        return type.IsValueType && Nullable.GetUnderlyingType(type) is null
            ? RuntimeHelpers.GetUninitializedObject(type)
            : null;
    }

    private void Report(IInterceptorSubject subject)
    {
        if (Interlocked.Exchange(ref _hasReported, 1) == 0)
        {
            try
            {
                Trace.TraceWarning(
                    $"The derived property {name} of {subject.GetType().Name} reads itself while it is evaluated and gives the default of its type instead.");
            }
            catch (Exception)
            {
                // A failing trace listener must not turn the reentry into an exception from the read.
            }
        }
    }
}
