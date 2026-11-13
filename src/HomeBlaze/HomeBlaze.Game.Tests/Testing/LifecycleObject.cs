using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// An object that counts how often it entered and left the graph, how often <see cref="OnAddedToWorld"/> ran and how
/// often the increment it schedules ran.
/// </summary>
[InterceptorSubject]
public partial class LifecycleObject : GameObject, ILifecycleHandler
{
    public int AttachCount { get; private set; }

    public int DetachCount { get; private set; }

    public int AddedCount { get; private set; }

    public int RunCount { get; private set; }

    /// <summary>
    /// Schedules an increment of <see cref="RunCount"/>; call on the simulation thread, as a schedule does.
    /// </summary>
    public IDisposable ScheduleIncrement(TimeSpan delay) => World.Schedule(this, delay, () => RunCount++);

    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (change.IsContextAttach)
        {
            AttachCount++;
        }

        if (change.IsContextDetach)
        {
            DetachCount++;
        }
    }

    protected override void OnAddedToWorld() => AddedCount++;
}
