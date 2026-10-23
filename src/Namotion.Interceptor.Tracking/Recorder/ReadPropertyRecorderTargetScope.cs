namespace Namotion.Interceptor.Tracking.Recorder;

/// <summary>
/// Restores the previous read target of the current thread when disposed, see
/// <see cref="ReadPropertyRecorder.RecordOnCurrentThread"/>.
/// </summary>
public readonly ref struct ReadPropertyRecorderTargetScope
{
    private readonly IReadPropertyRecorderTarget? _target;
    private readonly IReadPropertyRecorderTarget? _previousTarget;

    internal ReadPropertyRecorderTargetScope(IReadPropertyRecorderTarget target, IReadPropertyRecorderTarget? previousTarget)
    {
        _target = target;
        _previousTarget = previousTarget;
    }

    /// <summary>
    /// Restores the read target that was set when the scope started. Must run on the thread that started it.
    /// Does nothing when the target of the scope is no longer the current one, such as when disposed a second time.
    /// </summary>
    public void Dispose()
    {
        if (_target is not null)
        {
            ReadPropertyRecorder.RestoreCurrentThreadTarget(_target, _previousTarget);
        }
    }
}
