using HomeBlaze.Authorization.Data;
using HomeBlaze.Authorization.Extensions;
using HomeBlaze.Authorization.Roles;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Authorization.Services;

/// <summary>
/// Hosted service that seeds default roles and admin user on first startup.
/// </summary>
public class IdentitySeeding : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<IdentitySeeding> _logger;

    /// <summary>
    /// Default role hierarchy: each role includes the roles listed.
    /// </summary>
    private static readonly Dictionary<string, string[]> DefaultRoleHierarchy = new()
    {
        [DefaultRoles.Anonymous] = [],
        [DefaultRoles.Guest] = [DefaultRoles.Anonymous],
        [DefaultRoles.User] = [DefaultRoles.Guest],
        [DefaultRoles.Operator] = [DefaultRoles.User],
        [DefaultRoles.Supervisor] = [DefaultRoles.Operator],
        [DefaultRoles.Admin] = [DefaultRoles.Supervisor]
    };

    public IdentitySeeding(IServiceProvider serviceProvider, ILogger<IdentitySeeding> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        // Ensure database is created
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);

        // Seed roles and role compositions
        await SeedRolesAsync(dbContext, roleManager, cancellationToken);

        // Seed admin user if no users exist
        await SeedAdminUserAsync(userManager, cancellationToken);

        // Initialize role expander with seeded data
        // This must happen after seeding so the hierarchy is available
        var roleExpander = _serviceProvider.GetRequiredService<IRoleExpander>();
        await roleExpander.InitializeAsync();
        _logger.LogInformation("Role expander initialized with role hierarchy");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SeedRolesAsync(
        AuthorizationDbContext dbContext,
        RoleManager<IdentityRole> roleManager,
        CancellationToken cancellationToken)
    {
        // TODO: If roles.count > 0, dont run this block (same as with users)
        
        foreach (var (roleName, includedRoles) in DefaultRoleHierarchy)
        {
            // Create Identity role if it doesn't exist
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                var result = await roleManager.CreateAsync(new IdentityRole(roleName));
                if (result.Succeeded)
                {
                    _logger.LogInformation("Created role: {RoleName}", roleName);
                }
                else
                {
                    _logger.LogWarning(
                        "Failed to create role {RoleName}: {Errors}",
                        roleName,
                        result.GetErrorMessage());
                }
            }

            // Create role compositions for hierarchy
            foreach (var includedRole in includedRoles)
            {
                var existingComposition = await dbContext.RoleCompositions
                    .FirstOrDefaultAsync(
                        c => c.RoleName == roleName && c.IncludesRole == includedRole,
                        cancellationToken);

                if (existingComposition == null)
                {
                    dbContext.RoleCompositions.Add(new RoleComposition
                    {
                        RoleName = roleName,
                        IncludesRole = includedRole
                    });
                    _logger.LogInformation(
                        "Created role composition: {RoleName} includes {IncludesRole}",
                        roleName,
                        includedRole);
                }
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task SeedAdminUserAsync(
        UserManager<ApplicationUser> userManager,
        CancellationToken cancellationToken)
    {
        // Check if any users exist
        var userCount = await userManager.Users.CountAsync(cancellationToken);
        if (userCount > 0)
        {
            _logger.LogDebug("Users already exist, skipping admin user seeding");
            return;
        }

        // Create admin user with no password
        var adminUser = new ApplicationUser
        {
            UserName = "admin",
            Email = "admin@localhost",
            DisplayName = "Administrator",
            MustChangePassword = true,
            CreatedAt = DateTime.UtcNow,
            EmailConfirmed = true // Skip email confirmation for initial admin
        };

        // Create user without password - they must set one on first login
        var result = await userManager.CreateAsync(adminUser);
        if (!result.Succeeded)
        {
            _logger.LogError(
                "Failed to create admin user: {Errors}",
                result.GetErrorMessage());
            return;
        }

        _logger.LogWarning(
            "Created default admin user '{UserName}' with no password. " +
            "User must set a password on first login.",
            adminUser.UserName);

        // Assign Admin role
        var roleResult = await userManager.AddToRoleAsync(adminUser, DefaultRoles.Admin);
        if (roleResult.Succeeded)
        {
            _logger.LogInformation(
                "Assigned {Role} role to admin user",
                DefaultRoles.Admin);
        }
        else
        {
            _logger.LogWarning(
                "Failed to assign {Role} role to admin user: {Errors}",
                DefaultRoles.Admin,
                roleResult.GetErrorMessage());
        }
    }
}
