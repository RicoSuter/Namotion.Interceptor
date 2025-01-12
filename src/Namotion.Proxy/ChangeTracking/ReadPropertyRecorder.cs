using Namotion.Interceptor;
using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.ChangeTracking;

internal class ReadPropertyRecorder : IReadInterceptor
{
    internal static AsyncLocal<IDictionary<IInterceptor, List<HashSet<PropertyReference>>>> Scopes { get; } = new();

    public object? ReadProperty(ReadPropertyInterception context, Func<ReadPropertyInterception, object?> next)
    {
        if (Scopes.Value is not null)
        {
            lock (typeof(ReadPropertyRecorder))
            {
                if (Scopes.Value is not null &&
                    Scopes.Value.TryGetValue(context.Context, out var scopes))
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
