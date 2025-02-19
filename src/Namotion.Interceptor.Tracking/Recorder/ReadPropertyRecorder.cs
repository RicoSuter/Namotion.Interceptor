﻿namespace Namotion.Interceptor.Tracking.Recorder;

public class ReadPropertyRecorder : IReadInterceptor
{
    internal static AsyncLocal<IDictionary<ReadPropertyRecorder, List<HashSet<PropertyReference>>>> Scopes { get; } = new();
    
    public object? ReadProperty(ReadPropertyInterception context, Func<ReadPropertyInterception, object?> next)
    {
        if (Scopes.Value is not null)
        {
            lock (typeof(ReadPropertyRecorder))
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

        return next(context);
    }
    
    public void AttachTo(IInterceptorSubject subject)
    {
    }

    public void DetachFrom(IInterceptorSubject subject)
    {
    }
}
