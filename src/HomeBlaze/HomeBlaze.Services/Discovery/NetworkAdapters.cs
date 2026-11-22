using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HomeBlaze.Services.Discovery;

internal static class NetworkAdapters
{
    /// <summary>
    /// Gets the IPv4 addresses of the adapters that are up, are no loopback and support multicast, or none when the
    /// adapters cannot be listed.
    /// </summary>
    public static IReadOnlyList<IPAddress> GetMulticastIPv4Addresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                    adapter.SupportsMulticast &&
                    adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                .Select(unicastAddress => unicastAddress.Address)
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                .Distinct()
                .ToArray();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }
}
