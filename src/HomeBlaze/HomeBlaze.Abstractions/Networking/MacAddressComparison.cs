namespace HomeBlaze.Abstractions.Networking;

/// <summary>
/// Compares MAC addresses in the forms devices report them, such as <c>3C:8A:1F:32:E1:57</c> and <c>3c8a1f32e157</c>.
/// </summary>
public static class MacAddressComparison
{
    /// <summary>
    /// Whether two MAC addresses are the same, ignoring case and the separators <c>:</c> and <c>-</c>; false when
    /// either is null or blank.
    /// </summary>
    public static bool AreEqual(string? macAddress, string? otherMacAddress)
    {
        if (string.IsNullOrWhiteSpace(macAddress) || string.IsNullOrWhiteSpace(otherMacAddress))
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
