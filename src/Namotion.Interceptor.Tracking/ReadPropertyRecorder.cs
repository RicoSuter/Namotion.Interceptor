using Namotion.Interceptor;

namespace Namotion.Interception.Lifecycle;

public class ReadPropertyRecorder : IReadInterceptor
{
    private readonly IInterceptorCollection _interceptors;
    internal static AsyncLocal<IDictionary<IInterceptorCollection, List<HashSet<PropertyReference>>>> Scopes { get; } = new();

    public ReadPropertyRecorder(IInterceptorCollection interceptors)
    {
        _interceptors = interceptors;
    }
    
    public object? ReadProperty(ReadPropertyInterception context, Func<ReadPropertyInterception, object?> next)
    {
        if (Scopes.Value is not null)
        {
            lock (typeof(ReadPropertyRecorder))
            {
                if (Scopes.Value is not null &&
                    Scopes.Value.TryGetValue(_interceptors, out var scopes))
                {
                    foreach (var scope in scopes)
                    {
                        scope.Add(context.Property);
                    }
                }
            }
        }

        return next(context);
    }
}
