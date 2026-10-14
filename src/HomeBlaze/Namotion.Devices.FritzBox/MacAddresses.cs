namespace Namotion.Devices.FritzBox;

internal static class MacAddresses
{
    /// <returns>The address trimmed and in upper case, or null when it is empty.</returns>
    internal static string? Normalize(string? macAddress) =>
        string.IsNullOrWhiteSpace(macAddress) ? null : macAddress.Trim().ToUpperInvariant();
}
