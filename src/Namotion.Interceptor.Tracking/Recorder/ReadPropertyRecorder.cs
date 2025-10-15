using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Recorder;

public class ReadPropertyRecorder : IReadInterceptor
{
    internal static AsyncLocal<IDictionary<ReadPropertyRecorder, List<ReadPropertyRecorderScope>>> Scopes { get; } = new();
    
    public TProperty ReadProperty<TProperty>(ref PropertyReadContext context, ReadInterceptionDelegate<TProperty> next)
    {
        if (Scopes.Value is not null &&
            Scopes.Value.TryGetValue(this, out var scopes))
        {
            for (var index = 0; index < scopes.Count; index++)
            {
                var scope = scopes[index];
                scope.AddProperty(context.Property);
            }
        }

        return next(ref context);
    }
}
