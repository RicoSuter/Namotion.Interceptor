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

    /// <returns>The value, empty for an empty element and null when the device did not return it as text.</returns>
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
    public bool? GetBoolean(string name) => GetString(name)?.Trim().ToUpperInvariant() switch
    {
        "1" or "TRUE" or "YES" => true,
        "0" or "FALSE" or "NO" => false,
        _ => null
    };
}
