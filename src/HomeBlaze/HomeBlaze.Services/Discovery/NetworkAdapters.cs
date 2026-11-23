using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HomeBlaze.Services.Discovery;

internal static class NetworkAdapters
{
    /// <summary>
    /// Gets the IPv4 addresses of the adapters that are up, are no loopback and support multicast, or none when the
    /// adapters cannot be listed. An adapter that cannot be read is skipped without affecting the others.
    /// </summary>
    public static IReadOnlyList<IPAddress> GetMulticastIPv4Addresses() =>
        GetIPv4Addresses(static unicastAddress => unicastAddress.Address);

    /// <summary>
    /// Gets the broadcast addresses of the IPv4 subnets of the adapters that <see cref="GetMulticastIPv4Addresses"/>
    /// lists, each once and without point-to-point links, which have none, or none when the adapters cannot be listed.
    /// </summary>
    public static IReadOnlyList<IPAddress> GetIPv4BroadcastAddresses() =>
        GetIPv4Addresses(static unicastAddress => GetBroadcastAddress(unicastAddress.Address, unicastAddress.IPv4Mask));

    /// <summary>
    /// Gets the broadcast address of the subnet of <paramref name="address"/>, or null for a subnet of fewer than four
    /// addresses (prefix length /31 or /32), which is a point-to-point link.
    /// </summary>
    internal static IPAddress? GetBroadcastAddress(IPAddress address, IPAddress mask)
    {
        Span<byte> addressBytes = stackalloc byte[4];
        Span<byte> maskBytes = stackalloc byte[4];
        if (!address.TryWriteBytes(addressBytes, out var addressLength) || addressLength != 4 ||
            !mask.TryWriteBytes(maskBytes, out var maskLength) || maskLength != 4)
        {
            return null;
        }

        var hostBits = ~BinaryPrimitives.ReadUInt32BigEndian(maskBytes);
        if (hostBits < 3)
        {
            return null;
        }

        BinaryPrimitives.WriteUInt32BigEndian(addressBytes, BinaryPrimitives.ReadUInt32BigEndian(addressBytes) | hostBits);
        return new IPAddress(addressBytes);
    }

    private static IReadOnlyList<IPAddress> GetIPv4Addresses(Func<UnicastIPAddressInformation, IPAddress?> select)
    {
        NetworkInterface[] adapters;
        try
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (Exception exception) when (IsAdapterFailure(exception))
        {
            return [];
        }

        var addresses = new List<IPAddress>();
        foreach (var adapter in adapters)
        {
            try
            {
                // Some Linux virtual adapters report an unknown status although they work.
                if (adapter.OperationalStatus is (OperationalStatus.Up or OperationalStatus.Unknown) &&
                    adapter.SupportsMulticast &&
                    adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                {
                    foreach (var unicastAddress in adapter.GetIPProperties().UnicastAddresses)
                    {
                        if (unicastAddress.Address.AddressFamily == AddressFamily.InterNetwork && select(unicastAddress) is { } address)
                        {
                            addresses.Add(address);
                        }
                    }
                }
            }
            catch (Exception exception) when (IsAdapterFailure(exception))
            {
                // Skip this adapter only.
            }
        }

        return addresses.Distinct().ToArray();
    }

    private static bool IsAdapterFailure(Exception exception) =>
        exception is NetworkInformationException or IOException or UnauthorizedAccessException or PlatformNotSupportedException;
}
