using System.Net;
using System.Threading.RateLimiting;
using HomeBlaze.Authorization;
using HomeBlaze.Authorization.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>Adds the sign-in of people to the web application: the session cookie, the fallback policy and the request pipeline.</summary>
public static class AuthenticationWebExtensions
{
    /// <summary>The rate limiter policy of the sign-in, password and setup endpoints; <see cref="LoginRateLimit"/> describes its limits.</summary>
    public const string LoginRateLimitPolicy = "homeblaze-login";

    /// <summary>
    /// The rate limiter policy of the pages of the app and its live connection for requests that are not signed in;
    /// <see cref="UiStartRateLimit"/> describes its limits.
    /// </summary>
    public const string UiStartRateLimitPolicy = "homeblaze-ui-start";

    // Where a challenge or a refusal leads: the app, whose gate shows the dialog that signs in.
    private const string AppRootPath = "/";

    /// <summary>
    /// Adds the Identity application cookie as the session (<c>HttpOnly</c>, <c>SameSite=Lax</c>, secure as the request,
    /// 14 days sliding; a challenge or refusal redirects to the root of the app, where its dialogs sign in, or answers 401
    /// or 403 to a request that accepts JSON), the <see cref="HomeBlazeAccessRequirement"/> as the fallback policy, the
    /// services of the account endpoints, the <see cref="LoginRateLimitPolicy"/> and <see cref="UiStartRateLimitPolicy"/>
    /// and the trusted reverse proxies of <see cref="AuthenticationOptions"/>, and per circuit the revalidated
    /// authentication state, the <see cref="CurrentUser"/>, the <see cref="PolicyUserPermissions"/> as
    /// <see cref="IUserPermissions"/> and the <see cref="IUserAdministration"/> of that user. Requires
    /// <c>AddHomeBlazeAuthentication</c>; call it after the HomeBlaze services, whose permissions it replaces.
    /// </summary>
    public static IServiceCollection AddHomeBlazeWebAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "HomeBlaze.Session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromDays(14);
            options.SlidingExpiration = true;
            options.LoginPath = AppRootPath;
            options.LogoutPath = AccountEndpoints.LogoutPath;
            options.AccessDeniedPath = AppRootPath;
            options.Events.OnRedirectToLogin = context => RedirectOrAnswerAsync(context, StatusCodes.Status401Unauthorized);
            options.Events.OnRedirectToAccessDenied = context => RedirectOrAnswerAsync(context, StatusCodes.Status403Forbidden);
        });
        services.AddSessionValidation();

        services.AddAuthorization(options =>
        {
            var policy = new AuthorizationPolicyBuilder().AddRequirements(new HomeBlazeAccessRequirement()).Build();
            options.AddPolicy(HomeBlazeAccessRequirement.PolicyName, policy);
            options.FallbackPolicy = policy;
        });
        services.AddSingleton<IAuthorizationHandler, HomeBlazeAccessHandler>();
        services.AddCascadingAuthenticationState();
        services.AddPermissionServices();
        services.AddAccountEndpointServices();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(LoginRateLimitPolicy, LoginRateLimit.GetPartition);
            options.AddPolicy(UiStartRateLimitPolicy, UiStartRateLimit.GetPartition);
        });

        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<AuthenticationOptions>>((options, authentication) => ConfigureForwardedHeaders(options, authentication.Value));
        return services;
    }

    /// <summary>
    /// Applies the forwarded headers of the reverse proxies trusted in <see cref="AuthenticationOptions"/>, none when none
    /// is configured, and the configured <see cref="AuthenticationOptions.PathBase"/>. Call it first, before the
    /// exception handler, HSTS and HTTPS redirection, which read the scheme and host it sets.
    /// </summary>
    /// <exception cref="InvalidOperationException">A trusted network or proxy is not a valid network or address; the message names the setting.</exception>
    public static WebApplication UseHomeBlazeForwardedHeaders(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Read now, so that an invalid setting stops the start instead of failing the first request.
        if (app.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value.ForwardedHeaders != ForwardedHeaders.None)
        {
            app.UseForwardedHeaders();
        }

        var options = app.Services.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
        if (!string.IsNullOrEmpty(options.PathBase))
        {
            app.UsePathBase(options.PathBase);
            // Routing must match the path without the base, so it runs after UsePathBase instead of first.
            app.UseRouting();
        }

        return app;
    }

    /// <summary>
    /// Adds the request pipeline of sign-in: "authentication unavailable" while the database cannot be used,
    /// authentication, the refusal of every other endpoint than the app and the account endpoints while a person must
    /// change their password, authorization with the fallback policy, rate limiting and the origin check of the Blazor
    /// hub. Call it after HTTPS redirection and before antiforgery, map the account endpoints with
    /// <see cref="AccountEndpoints.MapHomeBlazeAccountEndpoints"/>, and the app with <see cref="WithHomeBlazeAccountGate"/>.
    /// </summary>
    public static WebApplication UseHomeBlazeWebAuthentication(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseMiddleware<AuthenticationUnavailableMiddleware>();
        app.UseAuthentication();
        app.UseMiddleware<MustChangePasswordMiddleware>();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.UseMiddleware<BlazorOriginCheckMiddleware>();
        return app;
    }

    /// <summary>
    /// Opens the pages of the app and its live connection to visitors who are not signed in, rate limited with
    /// <see cref="UiStartRateLimitPolicy"/>, as the gate of the app (<see cref="Components.AccountGate"/>) decides what
    /// they see: nothing but the dialog that signs them in, unless the built-in user <c>Anonymous</c> holds roles. Every
    /// other endpoint keeps the fallback policy <see cref="HomeBlazeAccessRequirement"/>.
    /// </summary>
    public static RazorComponentsEndpointConventionBuilder WithHomeBlazeAccountGate(this RazorComponentsEndpointConventionBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AllowAnonymous();
        builder.RequireRateLimiting(UiStartRateLimitPolicy);
        builder.WithMetadata(AppEndpointMetadata.Instance);
        return builder;
    }

    // The dialogs post with fetch and read the answer, which a redirect to the app would replace with its page.
    private static Task RedirectOrAnswerAsync(RedirectContext<CookieAuthenticationOptions> context, int statusCode)
    {
        if (context.Request.Headers.Accept.Any(accept => accept?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true))
        {
            context.Response.StatusCode = statusCode;
        }
        else
        {
            context.Response.Redirect(context.RedirectUri);
        }

        return Task.CompletedTask;
    }

    // Without a trusted proxy no forwarded header is applied, as any client could send one to spoof its address,
    // scheme or host.
    private static void ConfigureForwardedHeaders(ForwardedHeadersOptions options, AuthenticationOptions authentication)
    {
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        if (authentication.ForwardedHeadersKnownNetworks.Length == 0 && authentication.ForwardedHeadersKnownProxies.Length == 0)
        {
            options.ForwardedHeaders = ForwardedHeaders.None;
            return;
        }

        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        foreach (var network in authentication.ForwardedHeadersKnownNetworks)
        {
            options.KnownIPNetworks.Add(System.Net.IPNetwork.TryParse(network, out var parsed)
                ? parsed
                : throw new InvalidOperationException(
                    $"The setting {AuthenticationOptions.SectionName}:{nameof(AuthenticationOptions.ForwardedHeadersKnownNetworks)} contains '{network}', which is no network in CIDR notation such as 10.0.0.0/8."));
        }

        foreach (var proxy in authentication.ForwardedHeadersKnownProxies)
        {
            options.KnownProxies.Add(IPAddress.TryParse(proxy, out var parsed)
                ? parsed
                : throw new InvalidOperationException(
                    $"The setting {AuthenticationOptions.SectionName}:{nameof(AuthenticationOptions.ForwardedHeadersKnownProxies)} contains '{proxy}', which is no IP address."));
        }
    }

    /// <summary>
    /// Requires the key of the MCP user as a bearer token below <paramref name="path"/> while that user has a key, and
    /// refuses with 403 a request whose <c>Origin</c> header names another scheme, host or port than the request. The
    /// endpoint itself must allow anonymous access, as it is not authenticated with the session cookie.
    /// </summary>
    public static WebApplication UseHomeBlazeMcpClientKey(this WebApplication app, PathString path)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseWhen(context => context.Request.Path.StartsWithSegments(path), branch => branch.UseMiddleware<McpClientKeyMiddleware>());
        return app;
    }
}
