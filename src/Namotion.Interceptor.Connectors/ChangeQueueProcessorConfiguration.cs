namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Tuning configuration for a <see cref="ChangeQueueProcessor"/>.
/// </summary>
public sealed class ChangeQueueProcessorConfiguration
{
    /// <summary>
    /// Gets or sets the time to buffer changes before flushing. Default is 8ms.
    /// A value less than or equal to zero disables buffering (each change is processed individually).
    /// </summary>
    public TimeSpan BufferTime { get; set; } = TimeSpan.FromMilliseconds(8);

    /// <summary>
    /// Gets or sets how the processor reacts on overflow. Default is <see cref="OverflowBehavior.Unbounded"/>.
    /// </summary>
    public OverflowBehavior OverflowBehavior { get; set; } = OverflowBehavior.Unbounded;

    /// <summary>
    /// Gets or sets the bound on the buffered (pre-deduplication) change count. Required and must be
    /// positive when <see cref="OverflowBehavior"/> is <see cref="OverflowBehavior.DropOldest"/> or
    /// <see cref="OverflowBehavior.DropNewest"/>; ignored when <see cref="OverflowBehavior.Unbounded"/>.
    /// The queue coalesces by property at flush, so a burst on a single property can inflate this count.
    /// </summary>
    public int? MaxQueueSize { get; set; }

    /// <summary>
    /// Gets or sets a synchronous callback invoked once per overflow event (not once per dropped change).
    /// It runs on the producer thread and must be non-blocking: only record or flag, never do I/O inline.
    /// </summary>
    public Action<ChangeQueueOverflow>? OverflowHandler { get; set; }

    /// <summary>
    /// Validates the configuration and throws if invalid.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when a bounded behavior has no positive <see cref="MaxQueueSize"/>.</exception>
    public void Validate()
    {
        if (OverflowBehavior != OverflowBehavior.Unbounded && MaxQueueSize is not > 0)
        {
            throw new ArgumentException(
                $"MaxQueueSize must be a positive value when OverflowBehavior is {OverflowBehavior}, got: {(MaxQueueSize?.ToString() ?? "null")}.",
                nameof(MaxQueueSize));
        }
    }

    /// <summary>
    /// Creates a shallow copy. The base source derives a processor-owned configuration from the
    /// instance returned by an override, so that override's instance is never mutated and may be cached.
    /// </summary>
    internal ChangeQueueProcessorConfiguration Clone() => new()
    {
        BufferTime = BufferTime,
        OverflowBehavior = OverflowBehavior,
        MaxQueueSize = MaxQueueSize,
        OverflowHandler = OverflowHandler,
    };
}
