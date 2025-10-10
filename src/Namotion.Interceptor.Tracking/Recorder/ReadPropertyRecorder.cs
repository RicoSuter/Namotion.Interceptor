using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Recorder;

public class ReadPropertyRecorder : IReadInterceptor
{
    internal static AsyncLocal<IDictionary<ReadPropertyRecorder, List<HashSet<PropertyReference>>>> Scopes { get; } = new();
    internal static readonly object ScopesLock = new();
    
    public TProperty ReadProperty<TProperty>(ref PropertyReadContext context, ReadInterceptionDelegate<TProperty> next)
    {
        if (Scopes.Value is not null)
        {
            lock (ScopesLock)
            {
                if (Scopes.Value is not null &&
                    Scopes.Value.TryGetValue(this, out var scopes))
                {
                    foreach (var scope in scopes)
                    {
                        scope.Add(context.Property);
                    }
                }
            }
        }

        return next(ref context);
    }
}
