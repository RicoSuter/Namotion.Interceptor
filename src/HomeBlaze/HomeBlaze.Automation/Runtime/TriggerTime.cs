namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// An instant of the property triggers: <see cref="Now"/> on the wall clock, which expressions read and statuses show,
/// and <see cref="Elapsed"/> on the monotonic clock, which measures holds, so that a correction of the wall clock neither
/// ends nor extends a hold.
/// </summary>
internal readonly record struct TriggerTime(DateTimeOffset Now, TimeSpan Elapsed);
