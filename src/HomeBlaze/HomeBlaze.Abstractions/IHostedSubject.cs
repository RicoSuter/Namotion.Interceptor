using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions;

/// <summary>
/// Base interface for subjects with lifecycle status (services, devices, servers).
/// </summary>
public interface IHostedSubject
{
    /// <summary>
    /// Current lifecycle status.
    /// </summary>
    [State]
    ServiceStatus Status { get; }

    /// <summary>
    /// Human-readable status message (error details, progress info, etc.).
    /// </summary>
    [State]
    string? StatusMessage { get; }
}
