using HomeBlaze.Abstractions.Discovery;
using HomeBlaze.Abstractions.Networking;

namespace Namotion.Devices.MyStrom;

/// <summary>
/// A myStrom switch that announced itself on the local network.
/// </summary>
/// <param name="MacAddress">The MAC address in upper case without separators, as <see cref="MyStromSwitch.MacAddress"/> reports it, such as <c>02ABCD000010</c>.</param>
/// <param name="HostAddress">The IPv4 address to configure as <see cref="MyStromSwitch.HostAddress"/>.</param>
/// <param name="DeviceType">The literal device type, as <see cref="MyStromSwitch.DeviceType"/> reports it, such as <c>WS2</c>.</param>
public sealed record MyStromDiscoveredSwitch(string MacAddress, string HostAddress, string DeviceType) : IDiscoveryResult<MyStromSwitch>
{
    /// <inheritdoc />
    public string Key => MacAddress;

    /// <summary>
    /// The name to show, such as <c>myStrom Switch 000010</c>, with the last six digits of the MAC address because a
    /// switch announces no name.
    /// </summary>
    public string DisplayName => $"myStrom Switch {MacAddress.AsSpan(Math.Max(0, MacAddress.Length - 6))}";

    /// <inheritdoc />
    public string? Detail => $"{DeviceType}, {HostAddress}";

    /// <inheritdoc />
    public bool Matches(MyStromSwitch subject) =>
        MacAddressComparison.AreEqual(subject.MacAddress, MacAddress) ||
        string.Equals(subject.HostAddress?.Trim(), HostAddress, StringComparison.OrdinalIgnoreCase);
}
