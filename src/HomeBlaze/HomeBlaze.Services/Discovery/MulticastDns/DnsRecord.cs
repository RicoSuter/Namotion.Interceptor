using System.Net;

namespace HomeBlaze.Services.Discovery.MulticastDns;

/// <summary>
/// A record of a DNS answer, with its owner name without the trailing dot.
/// </summary>
internal abstract record DnsRecord(string Name);

internal sealed record PointerRecord(string Name, string Target) : DnsRecord(Name);

internal sealed record ServiceRecord(string Name, int Port, string Target) : DnsRecord(Name);

internal sealed record TextRecord(string Name, IReadOnlyList<string> Entries) : DnsRecord(Name);

internal sealed record AddressRecord(string Name, IPAddress Address) : DnsRecord(Name);
