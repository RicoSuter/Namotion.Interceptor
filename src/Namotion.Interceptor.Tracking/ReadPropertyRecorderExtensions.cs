using Namotion.Interceptor;

namespace Namotion.Interception.Lifecycle;

public static class ReadPropertyRecorderExtensions
{
    public static ReadPropertyRecorderScope StartRecordingPropertyReadCalls(this IInterceptorCollection context)
    {
        ReadPropertyRecorder.Scopes.Value =
            ReadPropertyRecorder.Scopes.Value ??
            new Dictionary<IInterceptorCollection, List<HashSet<PropertyReference>>>();

        var scope = new HashSet<PropertyReference>();
        ReadPropertyRecorder.Scopes.Value.TryAdd(context, new List<HashSet<PropertyReference>>());
        ReadPropertyRecorder.Scopes.Value[context].Add(scope);

        return new ReadPropertyRecorderScope(context, scope);
    }
}
