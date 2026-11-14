using System.Collections.Concurrent;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Recorder;

/// <summary>
/// A utility class for property read recording and an interceptor that automatically
/// records property reads to all active scopes on the current async context.
/// Uses AsyncLocal to ensure proper isolation across concurrent async operations
/// (e.g., Blazor components in different browser tabs/sessions).
/// A target set for the current thread with <see cref="RecordOnCurrentThread"/> takes precedence: while it is set,
/// reads on that thread go to the target and not to the async scopes.
/// </summary>
public class ReadPropertyRecorder : IReadInterceptor
{
    private static readonly AsyncLocal<List<ReadPropertyRecorderScope>?> _activeScopes = new();

    [ThreadStatic]
    private static IReadPropertyRecorderTarget? _currentThreadTarget;

    // The number of thread targets set on any thread. A read checks it before the thread static, whose access is a call
    // on some platforms, so that reads outside a thread target cost no more than without the feature.
    private static int _threadTargetCount;

    internal static int ThreadTargetCount => Volatile.Read(ref _threadTargetCount);

    internal static void RestoreCurrentThreadTarget(IReadPropertyRecorderTarget target, IReadPropertyRecorderTarget? previousTarget)
    {
        // A copy of a scope that is disposed again must not lower the count of the targets of other threads.
        if (ReferenceEquals(_currentThreadTarget, target))
        {
            _currentThreadTarget = previousTarget;
            Interlocked.Decrement(ref _threadTargetCount);
        }
    }

    /// <summary>
    /// Sends the property reads of the current thread to the given target until the returned scope is disposed.
    /// While a target is set, reads on this thread are not recorded by the scopes of <see cref="Start"/>.
    /// The scope must be disposed on the thread that started it.
    /// </summary>
    /// <param name="target">The target that receives the reads.</param>
    /// <returns>The scope that restores the previous target of the thread.</returns>
    public static ReadPropertyRecorderTargetScope RecordOnCurrentThread(IReadPropertyRecorderTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        // Counted before the target is set, so that the reads of this thread find the count above zero.
        Interlocked.Increment(ref _threadTargetCount);
        var previousTarget = _currentThreadTarget;
        _currentThreadTarget = target;
        return new ReadPropertyRecorderTargetScope(target, previousTarget);
    }

    /// <summary>
    /// Starts recording property reads to a new scope.
    /// Property reads are automatically recorded via the interceptor.
    /// Uses AsyncLocal for proper isolation across async contexts.
    /// </summary>
    /// <param name="properties">Optional preallocated properties dictionary.</param>
    /// <returns>The recording scope. Dispose to stop recording; this may happen on any async context.</returns>
    public static ReadPropertyRecorderScope Start(ConcurrentDictionary<PropertyReference, bool>? properties = null)
    {
        // Get or create the scopes list for this async context
        var scopes = _activeScopes.Value;
        if (scopes == null)
        {
            scopes = new List<ReadPropertyRecorderScope>();
            _activeScopes.Value = scopes;
        }

        var scope = new ReadPropertyRecorderScope(properties, scopes);
        lock (scopes)
        {
            scopes.Add(scope);
        }

        return scope;
    }

    /// <summary>
    /// Gets the number of scopes that record in the current async context.
    /// </summary>
    internal static int ActiveScopeCount
    {
        get
        {
            var scopes = _activeScopes.Value;
            if (scopes is null)
            {
                return 0;
            }

            lock (scopes)
            {
                return scopes.Count;
            }
        }
    }

    public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
    {
        // Recorded before the value is read, so that a change written after the read finds the property recorded.
        if (_threadTargetCount != 0)
        {
            var target = _currentThreadTarget;
            if (target is not null)
            {
                target.OnRead(context.Property);
                return next(ref context);
            }
        }

        var scopes = _activeScopes.Value;
        if (scopes is not null)
        {
            lock (scopes)
            {
                foreach (var scope in scopes)
                {
                    scope.AddProperty(context.Property);
                }
            }
        }

        return next(ref context);
    }
}
