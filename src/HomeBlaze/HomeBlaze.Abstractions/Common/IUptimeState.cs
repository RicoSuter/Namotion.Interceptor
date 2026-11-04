using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Common;

/// <summary>
/// Reports how long a device or process has been running since it last started.
/// </summary>
[SubjectAbstraction]
[Description("Reports how long a device or process has been running.")]
public interface IUptimeState
{
    /// <summary>
    /// The time since the last start, or null when unknown.
    /// </summary>
    [State(Position = 400)]
    TimeSpan? Uptime { get; }
}
