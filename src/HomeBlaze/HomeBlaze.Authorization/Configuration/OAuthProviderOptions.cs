namespace HomeBlaze.Authorization.Configuration;

/// <summary>
/// Configuration for an OAuth/OIDC authentication provider.
/// </summary>
public class OAuthProviderOptions
{
    /// <summary>
    /// Display name for the provider (e.g., "Google", "Microsoft").
    /// </summary>
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// OAuth client ID.
    /// </summary>
    public string ClientId { get; set; } = "";

    /// <summary>
    /// OAuth client secret.
    /// </summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>
    /// OpenID Connect authority URL.
    /// </summary>
    public string Authority { get; set; } = "";

    /// <summary>
    /// Whether this provider is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
