using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Authentication;

/// <summary>Runs authentication tasks on the services of <c>AddHomeBlazeAuthentication</c> without starting the host.</summary>
public static class AuthenticationServiceProviderExtensions
{
    /// <summary>
    /// Initializes the authentication database and resets the administrator with <see cref="AdminSeeding.ResetAdminAsync"/>,
    /// logging the outcome; a failure, such as a database that is busy or cannot be opened, is logged, not thrown.
    /// </summary>
    /// <returns>Whether the administrator was reset.</returns>
    public static async Task<bool> ResetAdminAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AdminSeeding));
        var database = services.GetRequiredService<AuthenticationDatabase>();
        await database.InitializeAsync(cancellationToken);
        if (!database.IsAvailable)
        {
            // The database logged the error.
            logger.LogError("The administrator was not reset: {Reason}", database.UnavailableReason);
            return false;
        }

        try
        {
            await AdminSeeding.ResetAdminAsync(services.GetRequiredService<IServiceScopeFactory>(), database, cancellationToken);
        }
        catch (Exception exception) when (IsResetFailure(exception))
        {
            logger.LogError(exception, "The administrator was not reset.");
            return false;
        }

        logger.LogWarning(
            "Reset the administrator {UserName}: sign in with an empty password and set a password. Every session has ended.",
            AdminSeeding.AdminUserName);
        return true;
    }

    // The documented failures of the reset, and those of a database that is busy or refuses a write.
    private static bool IsResetFailure(Exception exception) =>
        exception is InvalidOperationException or DbException or DbUpdateException ||
        exception is IOException or UnauthorizedAccessException;
}
