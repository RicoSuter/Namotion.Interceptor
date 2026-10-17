namespace Namotion.Interceptor.Tracking.Recorder;

/// <summary>
/// Restores the previous read target of the current thread when disposed, see
/// <see cref="ReadPropertyRecorder.RecordOnCurrentThread"/>.
/// </summary>
public readonly ref struct ReadPropertyRecorderTargetScope
{
    private readonly IReadPropertyRecorderTarget? _previousTarget;
    private readonly bool _isStarted;

    internal ReadPropertyRecorderTargetScope(IReadPropertyRecorderTarget? previousTarget)
    {
        _previousTarget = previousTarget;
        _isStarted = true;
    }

    /// <summary>
    /// Restores the read target that was set when the scope started. Must run on the thread that started it.
    /// </summary>
    public void Dispose()
    {
        if (_isStarted)
        {
            ReadPropertyRecorder.RestoreCurrentThreadTarget(_previousTarget);
        }
    }
}
