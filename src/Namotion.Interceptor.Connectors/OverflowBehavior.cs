namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Controls how <see cref="ChangeQueueProcessor"/> reacts when its buffered change queue
/// reaches <see cref="ChangeQueueProcessorConfiguration.MaxQueueSize"/>.
/// </summary>
public enum OverflowBehavior
{
    /// <summary>
    /// No bound is applied and <see cref="ChangeQueueProcessorConfiguration.MaxQueueSize"/> is ignored.
    /// This is the default and matches the original unbounded behavior.
    /// </summary>
    Unbounded = 0,

    /// <summary>
    /// On overflow, drop the oldest queued changes until the queue is back within the bound,
    /// so the newest change is retained.
    /// </summary>
    DropOldest,

    /// <summary>
    /// On overflow, reject the incoming change and keep what is already queued.
    /// </summary>
    DropNewest,
}
