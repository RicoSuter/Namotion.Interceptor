namespace Namotion.Interceptor.Tracking;

public static class ReadPropertyRecorderExtensions
{
    public static ReadPropertyRecorderScope StartRecordingPropertyReadCalls(this ReadPropertyRecorder recorder)
    {
        ReadPropertyRecorder.Scopes.Value =
            ReadPropertyRecorder.Scopes.Value ??
            new Dictionary<ReadPropertyRecorder, List<HashSet<PropertyReference>>>();

        var scope = new HashSet<PropertyReference>();
        ReadPropertyRecorder.Scopes.Value.TryAdd(recorder, new List<HashSet<PropertyReference>>());
        ReadPropertyRecorder.Scopes.Value[recorder].Add(scope);

        return new ReadPropertyRecorderScope(recorder, scope);
    }
}
