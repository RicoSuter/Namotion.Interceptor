using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Common;

/// <summary>
/// Interface for subjects that report software/firmware version and available updates.
/// </summary>
[SubjectAbstraction]
[Description("Reports software version and available updates.")]
public interface ISoftwareState
{
    /// <summary>
    /// The currently running software or firmware version.
    /// </summary>
    [State]
    string? SoftwareVersion { get; }

    /// <summary>
    /// The available software update version, or null if up to date.
    /// </summary>
    [State]
    string? AvailableSoftwareUpdate { get; }

    /// <summary>
    /// Whether a software update is available.
    /// </summary>
    bool HasSoftwareUpdate => AvailableSoftwareUpdate != null;
}
