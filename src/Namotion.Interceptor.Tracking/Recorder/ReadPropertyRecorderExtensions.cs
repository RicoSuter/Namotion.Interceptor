namespace Namotion.Interceptor.Tracking.Recorder;

public static class ReadPropertyRecorderExtensions
{
    /// <summary>
    /// Starts the recording of property read accesses.
    /// </summary>
    /// <param name="recorder">The recorder.</param>
    /// <returns>The recording scope.</returns>
    public static ReadPropertyRecorderScope StartPropertyAccessRecording(this ReadPropertyRecorder recorder)
    {
        ReadPropertyRecorder.Scopes.Value ??= new Dictionary<ReadPropertyRecorder, List<ReadPropertyRecorderScope>>();

        var scope = new ReadPropertyRecorderScope(recorder); // TODO(perf): Use pooled collection

        ReadPropertyRecorder.Scopes.Value.TryAdd(recorder, []);
        ReadPropertyRecorder.Scopes.Value[recorder].Add(scope);

        return scope;
    }
}
