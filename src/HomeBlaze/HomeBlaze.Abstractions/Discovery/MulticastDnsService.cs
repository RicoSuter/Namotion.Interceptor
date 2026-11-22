using System.Net;

namespace HomeBlaze.Abstractions.Discovery;

/// <summary>
/// An instance of a service type that answered an mDNS query.
/// </summary>
/// <param name="InstanceName">The instance label, such as <c>shellyplus1pm-a8032ab12345</c>, without the service type.</param>
/// <param name="HostName">The target of its SRV record, such as <c>shellyplus1pm-a8032ab12345.local</c>, or null.</param>
/// <param name="Port">The port of its SRV record, or 0.</param>
/// <param name="Addresses">Its IPv4 addresses, or the address its answer came from when it named none; never empty.</param>
/// <param name="Properties">Its TXT entries by key, ignoring case; a key without a value maps to an empty string.</param>
public sealed record MulticastDnsService(
    string InstanceName,
    string? HostName,
    int Port,
    IReadOnlyList<IPAddress> Addresses,
    IReadOnlyDictionary<string, string> Properties);
