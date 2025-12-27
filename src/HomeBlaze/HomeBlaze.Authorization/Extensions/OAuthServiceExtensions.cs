using HomeBlaze.Authorization.Configuration;
using HomeBlaze.Authorization.Data;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using System.Security.Claims;

namespace HomeBlaze.Authorization.Extensions;

/// <summary>
/// Extension methods for configuring OAuth/OIDC authentication providers.
/// </summary>
public static class OAuthServiceExtensions
{
    /// <summary>
    /// Adds OAuth/OIDC authentication providers configured in appsettings.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration root.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// Configuration format in appsettings.json:
    /// <code>
    /// {
    ///   "Authentication": {
    ///     "OAuth": {
    ///       "Google": {
    ///         "DisplayName": "Google",
    ///         "ClientId": "your-client-id",
    ///         "ClientSecret": "your-client-secret",
    ///         "Authority": "https://accounts.google.com",
    ///         "Enabled": true
    ///       }
    ///     }
    ///   }
    /// }
    /// </code>
    /// </remarks>
    public static IServiceCollection AddHomeBlazeOAuth(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var oauthSection = configuration.GetSection("Authentication:OAuth");
        if (!oauthSection.Exists())
        {
            // No OAuth configuration - skip silently
            return services;
        }

        var providers = oauthSection.GetChildren();
        var hasValidProvider = false;

        foreach (var providerSection in providers)
        {
            var providerName = providerSection.Key;
            var options = providerSection.Get<OAuthProviderOptions>();

            if (options == null || !options.Enabled)
            {
                continue;
            }

            // Validate required fields
            if (string.IsNullOrWhiteSpace(options.ClientId))
            {
                throw new InvalidOperationException(
                    $"OAuth provider '{providerName}' is enabled but ClientId is not configured.");
            }

            if (string.IsNullOrWhiteSpace(options.ClientSecret))
            {
                throw new InvalidOperationException(
                    $"OAuth provider '{providerName}' is enabled but ClientSecret is not configured.");
            }

            if (string.IsNullOrWhiteSpace(options.Authority))
            {
                throw new InvalidOperationException(
                    $"OAuth provider '{providerName}' is enabled but Authority is not configured.");
            }

            hasValidProvider = true;

            // Register the OpenIdConnect authentication handler
            services.AddAuthentication()
                .AddOpenIdConnect(providerName, options.DisplayName ?? providerName, oidcOptions =>
                {
                    oidcOptions.Authority = options.Authority;
                    oidcOptions.ClientId = options.ClientId;
                    oidcOptions.ClientSecret = options.ClientSecret;
                    oidcOptions.ResponseType = OpenIdConnectResponseType.Code;
                    oidcOptions.SaveTokens = true;
                    oidcOptions.GetClaimsFromUserInfoEndpoint = true;

                    // Request common scopes
                    oidcOptions.Scope.Clear();
                    oidcOptions.Scope.Add("openid");
                    oidcOptions.Scope.Add("profile");
                    oidcOptions.Scope.Add("email");

                    // Map external roles to internal roles
                    oidcOptions.Events = new OpenIdConnectEvents
                    {
                        OnTokenValidated = async context =>
                        {
                            await MapExternalRolesToInternal(context, providerName);
                        }
                    };
                });
        }

        if (hasValidProvider)
        {
            // Log that OAuth providers were configured
            services.AddLogging(builder =>
                builder.AddFilter("HomeBlaze.Authorization.OAuth", LogLevel.Information));
        }

        return services;
    }

    private static async Task MapExternalRolesToInternal(TokenValidatedContext context, string providerName)
    {
        if (context.Principal?.Identity is not ClaimsIdentity identity)
        {
            return;
        }

        var serviceProvider = context.HttpContext.RequestServices;
        var dbContext = serviceProvider.GetService<AuthorizationDbContext>();

        if (dbContext == null)
        {
            return;
        }

        // Get external role claims (providers use various claim types)
        var externalRoles = identity.Claims
            .Where(c => c.Type == ClaimTypes.Role ||
                       c.Type == "roles" ||
                       c.Type == "groups" ||
                       c.Type == "role")
            .Select(c => c.Value)
            .ToList();

        if (externalRoles.Count == 0)
        {
            return;
        }

        // Get role mappings from database
        var mappings = await dbContext.ExternalRoleMappings
            .Where(m => m.Provider == providerName && externalRoles.Contains(m.ExternalRole))
            .ToListAsync();

        // Add internal role claims
        foreach (var mapping in mappings)
        {
            if (!identity.HasClaim(ClaimTypes.Role, mapping.InternalRole))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, mapping.InternalRole));
            }
        }
    }
}
