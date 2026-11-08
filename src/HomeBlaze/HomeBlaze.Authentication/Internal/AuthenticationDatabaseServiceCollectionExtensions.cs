using HomeBlaze.Authentication.Data;
using HomeBlaze.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeBlaze.Authentication.Internal;

/// <summary>Registers the authentication database and Identity over it.</summary>
internal static class AuthenticationDatabaseServiceCollectionExtensions
{
    /// <summary>The name of the database file in the data directory when <see cref="AuthenticationOptions.Database"/> is not set.</summary>
    public const string DefaultDatabaseFileName = "authentication.db";

    public static void AddAuthenticationDatabase(this IServiceCollection services)
    {
        // The context is scoped, so that Identity's managers and a transaction share it within a scope; the options are a
        // singleton, so that the context factory of the role store, a singleton, can use them as well.
        services.AddDbContext<AuthenticationDbContext>(ConfigureDatabase, ServiceLifetime.Scoped, ServiceLifetime.Singleton);
        services.AddDbContextFactory<AuthenticationDbContext>(ConfigureDatabase);
        services.AddSingleton(serviceProvider => new AuthenticationDatabase(
            GetDatabasePath(serviceProvider),
            () => serviceProvider.GetRequiredService<IDbContextFactory<AuthenticationDbContext>>().CreateDbContext(),
            serviceProvider.GetRequiredService<ILogger<AuthenticationDatabase>>()));

        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = false;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
                options.Password.RequiredLength = 10;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireDigit = false;
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<AuthenticationDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders()
            // In place of the factory that AddRoles registers, which adds role claims: the roles are read from the user
            // store on every decision, and a change of roles keeps the security stamp, so role claims would go stale.
            .AddClaimsPrincipalFactory<UserClaimsPrincipalFactory<ApplicationUser>>();
    }

    private static void ConfigureDatabase(IServiceProvider serviceProvider, DbContextOptionsBuilder options) =>
        options.UseSqlite($"Data Source={GetDatabasePath(serviceProvider)}");

    private static string GetDatabasePath(IServiceProvider serviceProvider) =>
        serviceProvider.GetRequiredService<IOptions<AuthenticationOptions>>().Value.Database
        ?? Path.Combine(serviceProvider.GetRequiredService<RootManager>().DataDirectory, DefaultDatabaseFileName);
}
