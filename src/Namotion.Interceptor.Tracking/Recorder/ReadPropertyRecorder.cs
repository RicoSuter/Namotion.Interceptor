using System.Collections.Concurrent;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Recorder;

public class ReadPropertyRecorder : IReadInterceptor
{
    internal static AsyncLocal<ConcurrentBag<ReadPropertyRecorderScope>?> Scopes { get; } = new();
    
    /// <summary>
    /// Starts the recording of property read accesses.
    /// </summary>
    /// <param name="properties">The preallocated properties bag.</param>
    /// <returns>The recording scope.</returns>
    public static ReadPropertyRecorderScope Start(HashSet<PropertyReference>? properties = null)
    {
        Scopes.Value ??= [];

        var scope = new ReadPropertyRecorderScope(properties);
        Scopes.Value.Add(scope);
        return scope;
    }
    
    public TProperty ReadProperty<TProperty>(ref PropertyReadContext context, ReadInterceptionDelegate<TProperty> next)
    {
        var scopes = Scopes.Value;
        if (scopes is not null && !scopes.IsEmpty)
        {
            foreach (var scope in scopes)
            {
                if (!scope.IsDisposed)
                {
                    scope.AddProperty(context.Property);
                }
            }
        }

        return next(ref context);
    }
}
