namespace Namotion.Interceptor.Tracking.Recorder;

public static class ReadPropertyRecorderExtensions
{
    public static ReadPropertyRecorderScope StartPropertyAccessRecording(this ReadPropertyRecorder recorder)
    {
        ReadPropertyRecorder.Scopes.Value ??= new Dictionary<ReadPropertyRecorder, List<HashSet<PropertyReference>>>();

        var scope = new HashSet<PropertyReference>();

        ReadPropertyRecorder.Scopes.Value.TryAdd(recorder, []);
        ReadPropertyRecorder.Scopes.Value[recorder].Add(scope);

        return new ReadPropertyRecorderScope(recorder, scope);
    }
}
