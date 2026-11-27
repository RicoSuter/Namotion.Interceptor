namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Parsing for the OPC UA browse-name collection/dictionary convention, where a member's
/// browse name ends with a bracketed index or key, for example "Items[3]" or "Items[name]".
/// </summary>
internal static class OpcUaBrowseName
{
    /// <summary>
    /// Extracts the key of a member named <c>{containerBrowseName}[key]</c>. Returns false for any
    /// other browse name, including one that only ends with a bracketed segment, because a dictionary
    /// published with its raw keys can have a key such as "Pump[1]" that must be kept whole.
    /// </summary>
    public static bool TryGetMemberKey(string memberBrowseName, string containerBrowseName, out ReadOnlySpan<char> key)
    {
        var keyStart = containerBrowseName.Length + 1;
        if (memberBrowseName.Length > keyStart + 1 &&
            memberBrowseName.StartsWith(containerBrowseName, StringComparison.Ordinal) &&
            memberBrowseName[containerBrowseName.Length] == '[' &&
            memberBrowseName[^1] == ']')
        {
            key = memberBrowseName.AsSpan(keyStart, memberBrowseName.Length - keyStart - 1);
            return true;
        }

        key = default;
        return false;
    }

    /// <summary>
    /// Extracts the content of the trailing "[...]" segment of a browse name. Returns false
    /// when the name has no trailing bracket pair or the brackets are empty, since empty
    /// brackets carry no index or key information.
    /// </summary>
    public static bool TryGetBracketContent(string browseName, out ReadOnlySpan<char> content)
    {
        var bracketStart = browseName.LastIndexOf('[');
        if (bracketStart >= 0 && browseName.EndsWith(']'))
        {
            var contentLength = browseName.Length - bracketStart - 2;
            if (contentLength > 0)
            {
                content = browseName.AsSpan(bracketStart + 1, contentLength);
                return true;
            }
        }

        content = default;
        return false;
    }
}
