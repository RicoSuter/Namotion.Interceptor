namespace Namotion.Devices.FritzBox;

internal static class StringValues
{
    /// <returns>The value trimmed, or null when it is empty.</returns>
    internal static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
