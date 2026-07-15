namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Describes a single overflow event on a <see cref="ChangeQueueProcessor"/>. Passed to
/// <see cref="ChangeQueueProcessorConfiguration.OverflowHandler"/> once per overflow event
/// (not once per dropped change). <see cref="OverflowBehavior"/> is always
/// <see cref="OverflowBehavior.DropOldest"/> or <see cref="OverflowBehavior.DropNewest"/> here,
/// since <see cref="OverflowBehavior.Unbounded"/> never overflows.
/// </summary>
/// <param name="DroppedChangeCount">Number of changes dropped in this overflow event.</param>
/// <param name="OverflowBehavior">The behavior that produced the drop.</param>
/// <param name="MaxQueueSize">The configured queue bound that was exceeded.</param>
public readonly record struct ChangeQueueOverflow(
    int DroppedChangeCount,
    OverflowBehavior OverflowBehavior,
    int MaxQueueSize);
