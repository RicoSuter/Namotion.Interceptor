namespace HomeBlaze.Services;

/// <summary>
/// A link in the <c>links</c> of metadata: to a web address or to the absolute path of a page, subject or property.
/// </summary>
/// <param name="Title">The title to show; null to show what the link leads to.</param>
/// <param name="Url">The address as the metadata holds it, or null for a link to a path; only one that <see cref="IsWebAddress"/> accepts may be opened.</param>
/// <param name="Path">The absolute path of a page, subject or property, such as <c>/Devices/Pump/Temperature</c>, or null for a link to a web address.</param>
public sealed record MetadataLink(string? Title, string? Url, string? Path)
{
    /// <summary>Gets whether the text is an absolute http, https or mailto address, the only addresses a link may have.</summary>
    public static bool IsWebAddress(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto);
}
