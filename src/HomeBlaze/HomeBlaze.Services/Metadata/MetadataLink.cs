namespace HomeBlaze.Services.Metadata;

/// <summary>
/// A link in the <c>links</c> of metadata: to a web address or to the absolute path of a page, subject or property.
/// </summary>
/// <param name="Title">The title to show; null to show what the link leads to.</param>
/// <param name="Url">The address as the metadata holds it, or null for a link to a path; only one that <see cref="IsAllowedAddress"/> accepts may be opened.</param>
/// <param name="Path">The absolute path of a page, subject or property, such as <c>/Devices/Pump/Temperature</c>, or null for a link to a web address.</param>
public sealed record MetadataLink(string? Title, string? Url, string? Path)
{
    /// <summary>Gets whether the text is an absolute http, https or mailto address, the only addresses a link may have.</summary>
    public static bool IsAllowedAddress(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsAllowedScheme(uri.Scheme);

    /// <summary>Gets whether the text is an absolute http or https address, which opens in a new tab without opener and referrer.</summary>
    public static bool IsWebAddress(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Gets whether the scheme is http, https or mailto, without regard to case.</summary>
    public static bool IsAllowedScheme(string scheme)
        => scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
           scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
           scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase);
}
