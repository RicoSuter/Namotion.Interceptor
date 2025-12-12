using System.Collections.Concurrent;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Recorder;

/// <summary>
/// A utility class for property read recording and an interceptor that automatically
/// records property reads to all active scopes registered on the context.
/// </summary>
public class ReadPropertyRecorder : IReadInterceptor
{
    private const string ActiveScopesKey = "ReadPropertyRecorder.ActiveScopes";

    /// <summary>
    /// Starts recording property reads to a new scope.
    /// Property reads are automatically recorded via the interceptor.
    /// </summary>
    /// <param name="context">The context to record for.</param>
    /// <param name="properties">Optional preallocated properties dictionary.</param>
    /// <returns>The recording scope. Dispose to stop recording.</returns>
    public static ReadPropertyRecorderScope Start(IInterceptorSubjectContext context, ConcurrentDictionary<PropertyReference, bool>? properties = null)
    {
        var scope = new ReadPropertyRecorderScope(context, properties);

        // Register the scope with the context for automatic recording
        var scopes = context.GetOrAddData(ActiveScopesKey, () => new List<ReadPropertyRecorderScope>());
        lock (scopes)
        {
            scopes.Add(scope);
        }

        return scope;
    }

    internal static void RemoveScope(IInterceptorSubjectContext context, ReadPropertyRecorderScope scope)
    {
        if (context.TryGetData<List<ReadPropertyRecorderScope>>(ActiveScopesKey, out var scopes) && scopes is not null)
        {
            lock (scopes)
            {
                scopes.Remove(scope);
            }
        }
    }

    public TProperty ReadProperty<TProperty>(ref PropertyReadContext context, ReadInterceptionDelegate<TProperty> next)
    {
        var result = next(ref context);

        // Automatically record to all active scopes for this context
        var subjectContext = context.Property.Subject.Context;
        if (subjectContext.TryGetData<List<ReadPropertyRecorderScope>>(ActiveScopesKey, out var scopes) && scopes is not null)
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
