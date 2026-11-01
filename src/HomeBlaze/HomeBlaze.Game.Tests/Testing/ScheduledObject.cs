using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// An object that counts the runs of the increments it schedules and its calls of <see cref="OnAddedToWorld"/>, in which
/// it runs <see cref="OnAdded"/>.
/// </summary>
[InterceptorSubject]
public partial class ScheduledObject : GameObject
{
    private int _runCount;
    private int _addedCount;

    /// <summary>
    /// Readable from any thread, as are the other counts.
    /// </summary>
    public int RunCount => Volatile.Read(ref _runCount);

    public int AddedCount => Volatile.Read(ref _addedCount);

    public Action<ScheduledObject>? OnAdded { get; set; }

    /// <summary>
    /// Schedules an increment of <see cref="RunCount"/>, as an operation of the object does.
    /// </summary>
    public IDisposable ScheduleIncrement(TimeSpan delay) => World.Schedule(this, delay, () => Interlocked.Increment(ref _runCount));

    /// <summary>
    /// Schedules the action for another object through this object's game.
    /// </summary>
    public IDisposable ScheduleFor(GameObject gameObject, TimeSpan delay, Action action) => World.Schedule(gameObject, delay, action);

    protected override void OnAddedToWorld()
    {
        Interlocked.Increment(ref _addedCount);
        OnAdded?.Invoke(this);
    }
}
