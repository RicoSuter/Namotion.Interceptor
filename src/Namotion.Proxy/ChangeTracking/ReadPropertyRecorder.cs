using Namotion.Interceptor;
using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.ChangeTracking;

internal class ReadPropertyRecorder : IReadInterceptor
{
    private readonly IProxyContext _context;
    internal static AsyncLocal<IDictionary<IInterceptorCollection, List<HashSet<PropertyReference>>>> Scopes { get; } = new();

    public ReadPropertyRecorder(IProxyContext context)
    {
        _context = context;
    }
    
    public object? ReadProperty(ReadPropertyInterception context, Func<ReadPropertyInterception, object?> next)
    {
        if (Scopes.Value is not null)
        {
            lock (typeof(ReadPropertyRecorder))
            {
                if (Scopes.Value is not null &&
                    Scopes.Value.TryGetValue(_context, out var scopes))
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
