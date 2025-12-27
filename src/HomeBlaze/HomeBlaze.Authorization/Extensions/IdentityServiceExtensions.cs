using HomeBlaze.Authorization.Configuration;
using HomeBlaze.Authorization.Data;
using HomeBlaze.Authorization.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.Authorization.Extensions;

/// <summary>
/// Extension methods for configuring Identity and cookie authentication.
/// </summary>
public static class IdentityServiceExtensions
{
    /// <summary>
    /// Adds HomeBlaze Identity services with SQLite database storage.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">SQLite connection string. Defaults to "Data Source=identity.db".</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddHomeBlazeIdentity(
        this IServiceCollection services,
        string connectionString = "Data Source=identity.db")
    {
        // Register DbContext with SQLite
        services.AddDbContext<AuthorizationDbContext>(options =>
            options.UseSqlite(connectionString));

        // Register Identity with custom ApplicationUser
        services.AddIdentity<ApplicationUser, IdentityRole>(options =>
            {
                // Password settings - minimal requirements for home use
                // Note: MinimumLength should match PasswordStrengthUtility.MinimumLength in Blazor
                options.Password.RequiredLength = 8;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredUniqueChars = 1;

                // Lockout settings (Identity defaults)
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.AllowedForNewUsers = true;

                // User settings
                options.User.RequireUniqueEmail = false;
                options.SignIn.RequireConfirmedEmail = false;
                options.SignIn.RequireConfirmedAccount = false;
            })
            .AddEntityFrameworkStores<AuthorizationDbContext>()
            .AddDefaultTokenProviders();

        // Configure cookie authentication
        services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = "/login";
            options.LogoutPath = "/logout";
            options.AccessDeniedPath = "/access-denied";
            options.ExpireTimeSpan = TimeSpan.FromDays(14);
            options.SlidingExpiration = true;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest;
            options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;

            // Handle API requests differently (return 401 instead of redirect)
            options.Events = new CookieAuthenticationEvents
            {
                OnRedirectToLogin = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api"))
                    {
                        context.Response.StatusCode = 401;
                        return Task.CompletedTask;
                    }
                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                },
                OnRedirectToAccessDenied = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api"))
                    {
                        context.Response.StatusCode = 403;
                        return Task.CompletedTask;
                    }
                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                }
            };
        });

        // Register role expander (singleton - caches role hierarchy)
        services.AddSingleton<IRoleExpander, RoleExpander>();

        // Register claims transformation (scoped - runs per request)
        services.AddScoped<IClaimsTransformation, RoleHierarchyClaimsTransformation>();

        // Register identity seeding hosted service
        services.AddHostedService<IdentitySeeding>();

        // Register IHttpContextAccessor for accessing HttpContext from services
        services.AddHttpContextAccessor();

        return services;
    }
}
