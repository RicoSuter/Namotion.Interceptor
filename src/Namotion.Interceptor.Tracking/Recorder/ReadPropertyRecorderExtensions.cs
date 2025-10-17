namespace Namotion.Interceptor.Tracking.Recorder;

public static class ReadPropertyRecorderExtensions
{
    /// <summary>
    /// Starts the recording of property read accesses.
    /// </summary>
    /// <param name="recorder">The recorder.</param>
    /// <param name="properties">The preallocated properties bag.</param>
    /// <returns>The recording scope.</returns>
    public static ReadPropertyRecorderScope StartPropertyAccessRecording(this ReadPropertyRecorder recorder, HashSet<PropertyReference>? properties = null)
    {
        ReadPropertyRecorder.Scopes.Value ??= [];

        var scope = new ReadPropertyRecorderScope(properties);
        ReadPropertyRecorder.Scopes.Value.Add(scope);
        return scope;
    }
}
