using HomeBlaze.Authentication.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HomeBlaze.Authentication.Tests;

/// <summary>A database in a temporary file, deleted with its seed marker on dispose.</summary>
internal sealed class TestDatabase : IDisposable
{
    public TestDatabase()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"homeblaze-auth-{Guid.NewGuid():N}.db");
    }

    public string Path { get; }

    public static AuthenticationDbContext CreateContext(string path) =>
        new(new DbContextOptionsBuilder<AuthenticationDbContext>().UseSqlite(GetConnectionString(path)).Options);

    /// <summary>Closes the pooled connections of the database at <paramref name="path"/> opened with <see cref="CreateContext(string)"/>.</summary>
    // Never SqliteConnection.ClearAllPools: it disposes the connections of every database in the process, also the ones
    // that tests running in parallel are opening, which then fail with ObjectDisposedException.
    public static void ClearPool(string path)
    {
        using var connection = new SqliteConnection(GetConnectionString(path));
        SqliteConnection.ClearPool(connection);
    }

    public AuthenticationDbContext CreateContext() => CreateContext(Path);

    public void Dispose()
    {
        ClearPool(Path);
        File.Delete(Path);
        File.Delete(AdminSeeding.MarkerPath(Path));
    }

    private static string GetConnectionString(string path) => $"Data Source={path}";
}
