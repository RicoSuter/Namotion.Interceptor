using System.Globalization;

namespace Namotion.Devices.Upnp;

/// <summary>
/// The answer of an action: the values the device returned, by name.
/// </summary>
public sealed class UpnpSoapResponse
{
    private readonly List<(string Name, string? Value)> _values;

    internal UpnpSoapResponse(List<(string Name, string? Value)> values)
    {
        _values = values;
    }

    /// <returns>
    /// The value as the device sent it, including any whitespace; empty for an empty element and null when the
    /// device did not return it as text.
    /// </returns>
    public string? GetString(string name)
    {
        foreach (var (valueName, value) in _values)
        {
            if (valueName == name)
            {
                return value;
            }
        }

        return null;
    }

    /// <returns>The value, or null when the device did not return it or it is no whole number in range.</returns>
    public int? GetInt32(string name) =>
        int.TryParse(GetString(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <returns>The value, or null when the device did not return it or it is no whole number in range.</returns>
    public long? GetInt64(string name) =>
        long.TryParse(GetString(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <returns>
    /// The value, or null when the device did not return it or it is none of the UPnP boolean forms. The UPnP
    /// Device Architecture allows a boolean to be written as 1, true or yes for true and 0, false or no for false,
    /// matched case-insensitively.
    /// </returns>
    public bool? GetBoolean(string name)
    {
        var value = GetString(name);
        if (value is null)
        {
            return null;
        }

        var trimmed = value.AsSpan().Trim();
        if (IsOneOf(trimmed, "1", "true", "yes"))
        {
            return true;
        }

        if (IsOneOf(trimmed, "0", "false", "no"))
        {
            return false;
        }

        return null;
    }

    private static bool IsOneOf(ReadOnlySpan<char> value, string first, string second, string third) =>
        value.Equals(first, StringComparison.OrdinalIgnoreCase) ||
        value.Equals(second, StringComparison.OrdinalIgnoreCase) ||
        value.Equals(third, StringComparison.OrdinalIgnoreCase);
}
