using HomeBlaze.Abstractions.Discovery;

namespace Namotion.Devices.Ecowitt;

/// <summary>
/// An Ecowitt gateway that answered the broadcast request of its local API.
/// </summary>
/// <param name="MacAddress">The MAC address in upper case with colons, as <see cref="EcowittGateway.MacAddress"/> reports it, such as <c>3C:8A:1F:32:E1:57</c>.</param>
/// <param name="HostAddress">The IPv4 address to configure as <see cref="EcowittGateway.HostAddress"/>.</param>
/// <param name="DeviceName">The first word of the name the gateway announces, such as <c>GW2000A-WIFIE157</c>, or null when it announces none.</param>
/// <param name="FirmwareVersion">The rest of that name, such as <c>V3.3.3</c>, or null.</param>
public sealed record EcowittDiscoveredGateway(string MacAddress, string HostAddress, string? DeviceName, string? FirmwareVersion) : IDiscoveryResult<EcowittGateway>
{
    /// <inheritdoc />
    public string Key => MacAddress;

    /// <inheritdoc />
    public string DisplayName => DeviceName ?? "Ecowitt gateway";

    /// <inheritdoc />
    public string? Detail => FirmwareVersion is null ? HostAddress : $"{FirmwareVersion}, {HostAddress}";

    /// <inheritdoc />
    public bool Matches(EcowittGateway subject) =>
        IsSameMacAddress(subject.MacAddress, MacAddress) ||
        string.Equals(subject.HostAddress?.Trim(), HostAddress, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Compares two MAC addresses, ignoring case and the separators <c>:</c> and <c>-</c>.
    /// </summary>
    private static bool IsSameMacAddress(string? macAddress, string otherMacAddress)
    {
        if (string.IsNullOrWhiteSpace(macAddress))
        {
            return false;
        }

        var index = 0;
        var otherIndex = 0;
        while (true)
        {
            while (index < macAddress.Length && IsSeparator(macAddress[index]))
            {
                index++;
            }

            while (otherIndex < otherMacAddress.Length && IsSeparator(otherMacAddress[otherIndex]))
            {
                otherIndex++;
            }

            if (index == macAddress.Length || otherIndex == otherMacAddress.Length)
            {
                return index == macAddress.Length && otherIndex == otherMacAddress.Length;
            }

            if (char.ToUpperInvariant(macAddress[index]) != char.ToUpperInvariant(otherMacAddress[otherIndex]))
            {
                return false;
            }

            index++;
            otherIndex++;
        }
    }

    private static bool IsSeparator(char character) => character is ':' or '-';
}
