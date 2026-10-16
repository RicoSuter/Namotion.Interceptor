using System.Collections.Concurrent;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Recorder;

/// <summary>
/// A utility class for property read recording and an interceptor that automatically
/// records property reads to all active scopes on the current async context.
/// Uses AsyncLocal to ensure proper isolation across concurrent async operations
/// (e.g., Blazor components in different browser tabs/sessions).
/// </summary>
public class ReadPropertyRecorder : IReadInterceptor
{
    private static readonly AsyncLocal<List<ReadPropertyRecorderScope>?> _activeScopes = new();

    [ThreadStatic]
    private static IReadPropertyRecorderTarget? _currentThreadTarget;

    internal static IReadPropertyRecorderTarget? CurrentThreadTarget
    {
        get => _currentThreadTarget;
        set => _currentThreadTarget = value;
    }

    /// <summary>
    /// Sends the property reads of the current thread to the given target until the returned scope is disposed.
    /// While a target is set, reads on this thread are not recorded by the scopes of <see cref="Start"/>.
    /// The scope must be disposed on the same thread, which a <c>ref struct</c> ensures across awaits.
    /// </summary>
    /// <param name="target">The target that receives the reads.</param>
    /// <returns>The scope that restores the previous target of the thread.</returns>
    public static ReadPropertyRecorderTargetScope RecordOnCurrentThread(IReadPropertyRecorderTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var previousTarget = _currentThreadTarget;
        _currentThreadTarget = target;
        return new ReadPropertyRecorderTargetScope(previousTarget);
    }

    /// <summary>
    /// Starts recording property reads to a new scope.
    /// Property reads are automatically recorded via the interceptor.
    /// Uses AsyncLocal for proper isolation across async contexts.
    /// </summary>
    /// <param name="properties">Optional preallocated properties dictionary.</param>
    /// <returns>The recording scope. Dispose to stop recording.</returns>
    public static ReadPropertyRecorderScope Start(ConcurrentDictionary<PropertyReference, bool>? properties = null)
    {
        var scope = new ReadPropertyRecorderScope(properties);

        // Get or create the scopes list for this async context
        var scopes = _activeScopes.Value;
        if (scopes == null)
        {
            scopes = new List<ReadPropertyRecorderScope>();
            _activeScopes.Value = scopes;
        }

        lock (scopes)
        {
            scopes.Add(scope);
        }

        return scope;
    }

    internal static void RemoveScope(ReadPropertyRecorderScope scope)
    {
        var scopes = _activeScopes.Value;
        if (scopes is not null)
        {
            lock (scopes)
            {
                scopes.Remove(scope);
            }
        }
    }

    public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
    {
        // Recorded before the value is read, so that a change written after the read finds the property recorded.
        var target = _currentThreadTarget;
        if (target is not null)
        {
            target.OnRead(context.Property);
            return next(ref context);
        }

        var result = next(ref context);

        // Record to all active scopes in this async context
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

        return result;
    }
}
