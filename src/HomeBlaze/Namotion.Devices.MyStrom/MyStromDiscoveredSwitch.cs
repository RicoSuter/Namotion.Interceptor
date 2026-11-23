using HomeBlaze.Abstractions.Discovery;

namespace Namotion.Devices.MyStrom;

/// <summary>
/// A myStrom switch that announced itself on the local network.
/// </summary>
/// <param name="MacAddress">The MAC address in upper case without separators, as <see cref="MyStromSwitch.MacAddress"/> reports it, such as <c>FCF5C418ADAC</c>.</param>
/// <param name="HostAddress">The IPv4 address to configure as <see cref="MyStromSwitch.HostAddress"/>.</param>
/// <param name="DeviceType">The literal device type, as <see cref="MyStromSwitch.DeviceType"/> reports it, such as <c>WS2</c>.</param>
public sealed record MyStromDiscoveredSwitch(string MacAddress, string HostAddress, string DeviceType) : IDiscoveryResult<MyStromSwitch>
{
    /// <inheritdoc />
    public string Key => MacAddress;

    /// <summary>
    /// The name to show, such as <c>myStrom Switch 18ADAC</c>, with the last six digits of the MAC address because a
    /// switch announces no name.
    /// </summary>
    public string DisplayName => $"myStrom Switch {MacAddress.AsSpan(Math.Max(0, MacAddress.Length - 6))}";

    /// <inheritdoc />
    public string? Detail => $"{DeviceType}, {HostAddress}";

    /// <inheritdoc />
    public bool Matches(MyStromSwitch subject) =>
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
