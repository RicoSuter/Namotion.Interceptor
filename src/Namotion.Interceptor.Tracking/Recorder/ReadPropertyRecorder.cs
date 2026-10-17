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
