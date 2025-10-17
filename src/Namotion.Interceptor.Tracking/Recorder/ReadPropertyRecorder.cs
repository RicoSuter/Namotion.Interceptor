using System.Collections.Concurrent;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Recorder;

public class ReadPropertyRecorder : IReadInterceptor
{
    internal static AsyncLocal<ConcurrentBag<ReadPropertyRecorderScope>?> Scopes { get; } = new();
    
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
