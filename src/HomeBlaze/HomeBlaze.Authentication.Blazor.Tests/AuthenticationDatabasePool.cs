using HomeBlaze.Authentication.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.Authentication.Blazor.Tests;

/// <summary>Closes the pooled connections of the authentication database of one test, so that its files can be deleted.</summary>
internal static class AuthenticationDatabasePool
{
    /// <summary>Gets the connection string of the authentication database of <paramref name="services"/>, which must not be disposed yet.</summary>
    public static string GetConnectionString(IServiceProvider services)
    {
        using var context = services.GetRequiredService<IDbContextFactory<AuthenticationDbContext>>().CreateDbContext();
        return context.Database.GetConnectionString()!;
    }

    /// <summary>Closes the pooled connections of the database of <paramref name="connectionString"/>.</summary>
    // Never SqliteConnection.ClearAllPools: it disposes the connections of every database in the process, also the ones
    // that tests running in parallel are opening, whose requests then fail with ObjectDisposedException.
    public static void Clear(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(connection);
    }
}
