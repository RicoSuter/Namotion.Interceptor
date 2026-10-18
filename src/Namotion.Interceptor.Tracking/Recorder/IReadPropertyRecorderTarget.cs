namespace Namotion.Interceptor.Tracking.Recorder;

/// <summary>
/// Receives the property reads of the current thread while it is set with
/// <see cref="ReadPropertyRecorder.RecordOnCurrentThread"/>.
/// </summary>
public interface IReadPropertyRecorderTarget
{
    /// <summary>
    /// Called before the value of an intercepted property is read, on the reading thread.
    /// </summary>
    /// <param name="property">The property that is read.</param>
    void OnRead(PropertyReference property);
}
