using System.Collections.Concurrent;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Recorder;

public class ReadPropertyRecorder : IReadInterceptor
{
    public static AsyncLocal<ConcurrentDictionary<ReadPropertyRecorderScope, bool>?> Scopes { get; } = new();
    
    /// <summary>
    /// Starts the recording of property read accesses.
    /// </summary>
    /// <param name="properties">The preallocated properties bag.</param>
    /// <returns>The recording scope.</returns>
    public static ReadPropertyRecorderScope Start(ConcurrentDictionary<PropertyReference, bool>? properties = null)
    {
        Scopes.Value ??= [];

        var scope = new ReadPropertyRecorderScope(properties);
        Scopes.Value.TryAdd(scope, false);
        return scope;
    }
    
    /// <summary>
    /// Starts the recording of property read accesses.
    /// </summary>
    /// <returns>The recording scope.</returns>
    public static ReadPropertyRecorderScope Start(ReadPropertyRecorderScope scope)
    {
        Scopes.Value ??= [];
        Scopes.Value.TryAdd(scope, false);

        return scope;
    }
    
    public TProperty ReadProperty<TProperty>(ref PropertyReadContext context, ReadInterceptionDelegate<TProperty> next)
    {
        var scopes = Scopes.Value;
        if (scopes is not null && !scopes.IsEmpty)
        {
            foreach (var scope in scopes)
            {
                scope.Key.AddProperty(context.Property);
            }
        }

        return next(ref context);
    }
}
