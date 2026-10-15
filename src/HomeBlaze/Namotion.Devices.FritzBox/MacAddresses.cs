namespace Namotion.Devices.FritzBox;

internal static class MacAddresses
{
    private const int HexDigitCount = 12;

    /// <returns>The address trimmed and in upper case, or null when it is empty.</returns>
    internal static string? Normalize(string? macAddress) =>
        string.IsNullOrWhiteSpace(macAddress) ? null : macAddress.Trim().ToUpperInvariant();

    /// <returns>
    /// The twelve hex digits of the address in upper case, without the separators of any notation
    /// (<c>AA:BB:…</c>, <c>aa-bb-…</c>, <c>aabb.ccdd.eeff</c>, <c>AABB…</c>), or null when it is not a MAC address.
    /// </returns>
    internal static string? ToHexDigits(string? macAddress)
    {
        if (string.IsNullOrWhiteSpace(macAddress))
        {
            return null;
        }

        Span<char> digits = stackalloc char[HexDigitCount];
        var length = 0;
        foreach (var character in macAddress.AsSpan().Trim())
        {
            if (character is ':' or '-' or '.')
            {
                continue;
            }

            if (length == HexDigitCount || !char.IsAsciiHexDigit(character))
            {
                return null;
            }

            digits[length++] = char.ToUpperInvariant(character);
        }

        return length == HexDigitCount ? new string(digits) : null;
    }
}
