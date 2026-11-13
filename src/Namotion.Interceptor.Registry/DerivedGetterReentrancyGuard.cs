using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Registry;

/// <summary>
/// The getter of a dynamic derived property that gives the default of the property's type when it is evaluated again
/// while it evaluates on the same thread, as a getter that reads itself through other properties would otherwise
/// recurse until the stack overflows. Reports each property once through <see cref="Trace"/>.
/// </summary>
internal sealed class DerivedGetterReentrancyGuard(string name, Type type, Func<IInterceptorSubject, object?> getValue)
{
    // The getters that this thread evaluates, innermost last.
    [ThreadStatic]
    private static List<(DerivedGetterReentrancyGuard Guard, IInterceptorSubject Subject)>? _active;

    // A boxed default, so that a property of a non-nullable value type reads a value of its type.
    private readonly object? _defaultValue = type.IsValueType && Nullable.GetUnderlyingType(type) is null
        ? RuntimeHelpers.GetUninitializedObject(type)
        : null;

    private int _hasReported;

    public object? GetValue(IInterceptorSubject subject)
    {
        var active = _active ??= [];
        foreach (var entry in active)
        {
            if (ReferenceEquals(entry.Guard, this) && ReferenceEquals(entry.Subject, subject))
            {
                Report(subject);
                return _defaultValue;
            }
        }

        active.Add((this, subject));
        try
        {
            return getValue(subject);
        }
        finally
        {
            active.RemoveAt(active.Count - 1);
        }
    }

    private void Report(IInterceptorSubject subject)
    {
        if (Interlocked.Exchange(ref _hasReported, 1) == 0)
        {
            Trace.TraceWarning(
                $"The derived property {name} of {subject.GetType().Name} reads itself while it is evaluated and gives the default of its type instead.");
        }
    }
}
