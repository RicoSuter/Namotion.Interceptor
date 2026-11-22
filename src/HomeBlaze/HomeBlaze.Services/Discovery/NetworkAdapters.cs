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
    public static IReadOnlyList<IPAddress> GetMulticastIPv4Addresses()
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
                    addresses.AddRange(adapter.GetIPProperties().UnicastAddresses
                        .Select(unicastAddress => unicastAddress.Address)
                        .Where(address => address.AddressFamily == AddressFamily.InterNetwork));
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
