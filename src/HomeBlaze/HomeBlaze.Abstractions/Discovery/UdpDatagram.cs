using System.Net;

namespace HomeBlaze.Abstractions.Discovery;

/// <summary>
/// A datagram that <see cref="IUdpDiscovery"/> received.
/// </summary>
/// <param name="Payload">The bytes of the datagram, at most 1024 and possibly none; a copy that stays valid after the enumeration moves on.</param>
/// <param name="SenderAddress">The IPv4 address the datagram came from.</param>
public readonly record struct UdpDatagram(ReadOnlyMemory<byte> Payload, IPAddress SenderAddress);
