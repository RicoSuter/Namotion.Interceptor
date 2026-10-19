using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Networking;

/// <summary>
/// A device that is reached at a configured host name or IP address.
/// </summary>
[SubjectAbstraction]
[Description("Device reached at a configured host name or IP address.")]
public interface INetworkHost
{
    /// <summary>
    /// The host name or IP address of the device, optionally with a port, or null when not configured.
    /// </summary>
    string? HostAddress { get; }
}
