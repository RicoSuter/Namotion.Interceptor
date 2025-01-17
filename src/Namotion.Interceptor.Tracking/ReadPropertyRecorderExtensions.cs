using Namotion.Interceptor;

namespace Namotion.Interception.Lifecycle;

public static class ReadPropertyRecorderExtensions
{
    public static ReadPropertyRecorderScope StartRecordingPropertyReadCalls(this IInterceptorProvider provider)
    {
        ReadPropertyRecorder.Scopes.Value =
            ReadPropertyRecorder.Scopes.Value ??
            new Dictionary<IInterceptorProvider, List<HashSet<PropertyReference>>>();

        var scope = new HashSet<PropertyReference>();
        ReadPropertyRecorder.Scopes.Value.TryAdd(provider, new List<HashSet<PropertyReference>>());
        ReadPropertyRecorder.Scopes.Value[provider].Add(scope);

        return new ReadPropertyRecorderScope(provider, scope);
    }
}
