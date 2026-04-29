using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions;

/// <summary>
/// Base interface for subjects with lifecycle status (services, devices, servers).
/// </summary>
[SubjectAbstraction]
[Description("Subject with lifecycle status tracking (running, stopped, error).")]
public interface IMonitoredService
{
    /// <summary>
    /// Current lifecycle status.
    /// </summary>
    [State(Position = 910)]
    ServiceStatus Status { get; }

    /// <summary>
    /// Human-readable status message (error details, progress info, etc.).
    /// </summary>
    [State(Position = 911)]
    string? StatusMessage { get; }
}
