using HomeBlaze.Authentication.Data;
using HomeBlaze.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.Authentication;

/// <summary>
/// Seeds the built-in roles and users and the first administrator, and resets the administrator. A marker file next to
/// the database records that seeding happened, so that a lost or replaced database never seeds the empty password
/// again.
/// </summary>
public static class AdminSeeding
{
    /// <summary>The name of the seeded administrator.</summary>
    public const string AdminUserName = "admin";

    internal const string SeedingFailedReason = "The authentication database could not be seeded.";

    internal const string MarkerWithoutUsersReason =
        "The authentication database has no users although it was set up before. Run HomeBlaze once with --reset-admin.";

    /// <summary>Gets the path of the seed marker of the database at <paramref name="databasePath"/>.</summary>
    public static string MarkerPath(string databasePath)
    {
        ArgumentNullException.ThrowIfNull(databasePath);

        return databasePath + ".seeded";
    }

    /// <summary>
    /// Creates the missing built-in role rows and built-in users, then seeds <see cref="AdminUserName"/> with
    /// <see cref="BuiltInRoles.EverythingName"/> and an empty password and writes the seed marker when no person exists
    /// and no marker does. When the marker exists but no person does, nothing is seeded and <paramref name="database"/>
    /// is marked unavailable. Nothing is done while <paramref name="database"/> is unavailable.
    /// </summary>
    /// <param name="scopeFactory">Creates the scope of Identity's user and role managers and the database context.</param>
    /// <param name="database">The initialized authentication database.</param>
    /// <param name="cancellationToken">Cancels the seeding.</param>
    /// <exception cref="Exception">
    /// Any failure, such as a refused row or a marker that cannot be written: nothing is stored, <paramref name="database"/>
    /// is marked unavailable and the exception is rethrown.
    /// </exception>
    public static async Task<SeedOutcome> SeedAsync(IServiceScopeFactory scopeFactory, AuthenticationDatabase database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(database);

        if (!database.IsAvailable)
        {
            return SeedOutcome.DatabaseUnavailable;
        }

        var markerPath = MarkerPath(database.Path);
        try
        {
            var outcome = await RunInTransactionAsync(scopeFactory, async (userManager, roleManager) =>
            {
                await EnsureBuiltInsAsync(userManager, roleManager);
                if (await userManager.Users.AnyAsync(user => !user.IsBuiltIn, cancellationToken))
                {
                    return SeedOutcome.AlreadyHasUsers;
                }

                if (File.Exists(markerPath))
                {
                    return SeedOutcome.MarkerWithoutUsers;
                }

                var admin = new ApplicationUser { UserName = AdminUserName, AllowsEmptyPassword = true, MustChangePassword = true, LockoutEnabled = true };
                ThrowIfFailed(await userManager.CreateAsync(admin));
                ThrowIfFailed(await userManager.AddToRoleAsync(admin, BuiltInRoles.EverythingName));
                // Before the commit: a marker that cannot be written rolls the admin back, and a commit that fails
                // after it leaves a marker without users, which refuses to seed again.
                await WriteMarkerAsync(markerPath, cancellationToken);
                return SeedOutcome.Seeded;
            }, cancellationToken);

            if (outcome == SeedOutcome.MarkerWithoutUsers)
            {
                database.MarkUnavailable(MarkerWithoutUsersReason);
            }
            else if (outcome == SeedOutcome.AlreadyHasUsers)
            {
                // For a database without a marker, such as one created before markers, so that losing it is detected.
                await WriteMarkerAsync(markerPath, cancellationToken);
            }

            return outcome;
        }
        catch
        {
            // The caller logs the exception; the reason names no details, as visitors who are not signed in see it.
            database.MarkUnavailable(SeedingFailedReason);
            throw;
        }
    }

    /// <summary>
    /// Creates <see cref="AdminUserName"/> when missing, sets its roles to <see cref="BuiltInRoles.EverythingName"/> alone, clears its
    /// password so that it signs in with an empty one and must set a password, unlocks it, updates the security stamp
    /// of every user so that every session ends, and writes the seed marker. The missing built-in roles and users are
    /// created too.
    /// </summary>
    /// <param name="scopeFactory">Creates the scope of Identity's user and role managers and the database context.</param>
    /// <param name="database">The initialized authentication database.</param>
    /// <param name="cancellationToken">Cancels the reset.</param>
    /// <exception cref="InvalidOperationException">The database refused a change; the message says why and no user is changed.</exception>
    /// <exception cref="IOException">The seed marker cannot be written; no user is changed.</exception>
    /// <exception cref="UnauthorizedAccessException">The seed marker cannot be written; no user is changed.</exception>
    public static async Task ResetAdminAsync(IServiceScopeFactory scopeFactory, AuthenticationDatabase database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(database);

        await RunInTransactionAsync(scopeFactory, async (userManager, roleManager) =>
        {
            await EnsureBuiltInsAsync(userManager, roleManager);

            var admin = await userManager.FindByNameAsync(AdminUserName);
            if (admin is null)
            {
                admin = new ApplicationUser { UserName = AdminUserName, LockoutEnabled = true };
                ThrowIfFailed(await userManager.CreateAsync(admin));
            }

            if (await userManager.HasPasswordAsync(admin))
            {
                ThrowIfFailed(await userManager.RemovePasswordAsync(admin));
            }

            admin.AllowsEmptyPassword = true;
            admin.MustChangePassword = true;
            ThrowIfFailed(await userManager.UpdateAsync(admin));
            // Identity refuses to change the lockout end of a user whose lockout is disabled.
            ThrowIfFailed(await userManager.SetLockoutEnabledAsync(admin, true));
            ThrowIfFailed(await userManager.SetLockoutEndDateAsync(admin, null));
            ThrowIfFailed(await userManager.ResetAccessFailedCountAsync(admin));
            // Only everything, so that no other role, such as one that denies administer, keeps the administrator from
            // administering.
            var otherRoles = (await userManager.GetRolesAsync(admin))
                .Where(role => !string.Equals(role, BuiltInRoles.EverythingName, StringComparison.Ordinal))
                .ToArray();
            if (otherRoles.Length > 0)
            {
                ThrowIfFailed(await userManager.RemoveFromRolesAsync(admin, otherRoles));
            }

            if (!await userManager.IsInRoleAsync(admin, BuiltInRoles.EverythingName))
            {
                ThrowIfFailed(await userManager.AddToRoleAsync(admin, BuiltInRoles.EverythingName));
            }

            foreach (var user in await userManager.Users.ToListAsync(cancellationToken))
            {
                ThrowIfFailed(await userManager.UpdateSecurityStampAsync(user));
            }

            await WriteMarkerAsync(MarkerPath(database.Path), cancellationToken);
            return true;
        }, cancellationToken);
    }

    private static async Task<T> RunInTransactionAsync<T>(
        IServiceScopeFactory scopeFactory,
        Func<UserManager<ApplicationUser>, RoleManager<ApplicationRole>, Task<T>> steps,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthenticationDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var result = await steps(userManager, roleManager);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task EnsureBuiltInsAsync(UserManager<ApplicationUser> userManager, RoleManager<ApplicationRole> roleManager)
    {
        foreach (var role in BuiltInRoles.All)
        {
            if (!await roleManager.RoleExistsAsync(role.Name))
            {
                ThrowIfFailed(await roleManager.CreateAsync(new ApplicationRole { Name = role.Name, IsBuiltIn = true }));
            }
        }

        await EnsureBuiltInUserAsync(userManager, NameRules.AnonymousUserName, []);
        await EnsureBuiltInUserAsync(userManager, NameRules.McpUserName, [BuiltInRoles.ReaderName]);
    }

    // Roles are given only on creation, so that the roles a person later gives the user are kept.
    private static async Task EnsureBuiltInUserAsync(UserManager<ApplicationUser> userManager, string userName, string[] roleNames)
    {
        if (await userManager.FindByNameAsync(userName) is not null)
        {
            return;
        }

        var user = new ApplicationUser { UserName = userName, IsBuiltIn = true };
        ThrowIfFailed(await userManager.CreateAsync(user));
        // Identity enables lockout for every new user; a built-in user cannot sign in, so it is never locked out.
        ThrowIfFailed(await userManager.SetLockoutEnabledAsync(user, false));
        if (roleNames.Length > 0)
        {
            ThrowIfFailed(await userManager.AddToRolesAsync(user, roleNames));
        }
    }

    private static async Task WriteMarkerAsync(string markerPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(markerPath))
        {
            await File.WriteAllTextAsync(markerPath, "seeded", cancellationToken);
        }
    }

    private static void ThrowIfFailed(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join(" ", result.Errors.Select(error => error.Description)));
        }
    }
}

/// <summary>The outcome of <see cref="AdminSeeding.SeedAsync"/>.</summary>
public enum SeedOutcome
{
    /// <summary>No person and no seed marker existed: the administrator was seeded and the marker written.</summary>
    Seeded,

    /// <summary>A person existed, so no administrator was seeded.</summary>
    AlreadyHasUsers,

    /// <summary>The seed marker existed but no person did: nothing was seeded and the database is marked unavailable.</summary>
    MarkerWithoutUsers,

    /// <summary>The database was unavailable, so nothing was done.</summary>
    DatabaseUnavailable
}
