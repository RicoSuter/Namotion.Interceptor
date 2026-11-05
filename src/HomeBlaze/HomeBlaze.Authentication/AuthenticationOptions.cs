namespace HomeBlaze.Authentication;

/// <summary>The <c>Authentication</c> settings of <c>appsettings</c>.</summary>
public sealed class AuthenticationOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Authentication";

    /// <summary>The path of the database file; null for <c>authentication.db</c> in the data directory.</summary>
    public string? Database { get; set; }

    /// <summary>Networks (CIDR) of reverse proxies whose forwarded headers are trusted.</summary>
    public string[] ForwardedHeadersKnownNetworks { get; set; } = [];

    /// <summary>Addresses of reverse proxies whose forwarded headers are trusted.</summary>
    public string[] ForwardedHeadersKnownProxies { get; set; } = [];

    /// <summary>The base path when served below a path of a reverse proxy, such as <c>/homeblaze</c>; null for none.</summary>
    public string? PathBase { get; set; }
}
