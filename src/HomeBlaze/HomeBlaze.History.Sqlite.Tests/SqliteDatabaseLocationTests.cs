using HomeBlaze.History.Sqlite;

namespace HomeBlaze.History.Sqlite.Tests;

/// <summary>
/// Unit tests for the pure <see cref="SqliteDatabaseLocation.Resolve"/> path resolver. Each test injects
/// an explicit base directory so the result never depends on the real LocalApplicationData folder.
/// </summary>
public sealed class SqliteDatabaseLocationTests
{
    private const string BaseDirectory = "/base";

    [Fact]
    public void WhenConfiguredPathIsNull_ThenResolvesToDefaultFolderUnderBase()
    {
        // Arrange
        string? configuredPath = null;

        // Act
        var resolved = SqliteDatabaseLocation.Resolve(configuredPath, BaseDirectory);

        // Assert
        Assert.Equal(Path.Combine(BaseDirectory, SqliteDatabaseLocation.DefaultFolderName), resolved);
    }

    [Fact]
    public void WhenConfiguredPathIsEmpty_ThenResolvesToDefaultFolderUnderBase()
    {
        // Arrange
        var configuredPath = string.Empty;

        // Act
        var resolved = SqliteDatabaseLocation.Resolve(configuredPath, BaseDirectory);

        // Assert
        Assert.Equal(Path.Combine(BaseDirectory, SqliteDatabaseLocation.DefaultFolderName), resolved);
    }

    [Fact]
    public void WhenConfiguredPathIsWhitespace_ThenResolvesToDefaultFolderUnderBase()
    {
        // Arrange
        var configuredPath = "   ";

        // Act
        var resolved = SqliteDatabaseLocation.Resolve(configuredPath, BaseDirectory);

        // Assert
        Assert.Equal(Path.Combine(BaseDirectory, SqliteDatabaseLocation.DefaultFolderName), resolved);
    }

    [Fact]
    public void WhenConfiguredPathIsRelative_ThenResolvesUnderBase()
    {
        // Arrange
        var configuredPath = "MyHistory";

        // Act
        var resolved = SqliteDatabaseLocation.Resolve(configuredPath, BaseDirectory);

        // Assert
        Assert.Equal(Path.Combine(BaseDirectory, "MyHistory"), resolved);
    }

    [Fact]
    public void WhenConfiguredPathIsAbsolute_ThenReturnedUnchanged()
    {
        // Arrange
        var configuredPath = Path.Combine(Path.GetTempPath(), "abs-hist");

        // Act
        var resolved = SqliteDatabaseLocation.Resolve(configuredPath, BaseDirectory);

        // Assert
        Assert.Equal(configuredPath, resolved);
    }

    [Fact]
    public void WhenConfiguredPathHasSurroundingWhitespace_ThenTrimmedBeforeResolving()
    {
        // Arrange
        var configuredPath = "  MyHistory  ";

        // Act
        var resolved = SqliteDatabaseLocation.Resolve(configuredPath, BaseDirectory);

        // Assert
        Assert.Equal(Path.Combine(BaseDirectory, "MyHistory"), resolved);
    }

    [Fact]
    public void WhenDataDirectoryIsSet_ThenBaseDirectoryIsHistoryUnderDataDirectory()
    {
        // Arrange
        var dataDirectory = Path.Combine(Path.GetTempPath(), "homeblaze-instance");

        // Act
        var baseDirectory = SqliteDatabaseLocation.DefaultBaseDirectory(dataDirectory);

        // Assert
        Assert.Equal(Path.Combine(dataDirectory, "History"), baseDirectory);
    }

    [Fact]
    public void WhenDataDirectoryIsNull_ThenBaseDirectoryIsLocalApplicationData()
    {
        // Act
        var baseDirectory = SqliteDatabaseLocation.DefaultBaseDirectory(null);

        // Assert
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeBlaze"),
            baseDirectory);
    }

    [Fact]
    public void WhenDataDirectoryIsSetAndPathIsEmpty_ThenStoreIsHistorySqliteUnderDataDirectory()
    {
        // Arrange
        var dataDirectory = Path.Combine(Path.GetTempPath(), "homeblaze-instance");

        // Act
        var resolved = SqliteDatabaseLocation.Resolve(string.Empty, SqliteDatabaseLocation.DefaultBaseDirectory(dataDirectory));

        // Assert
        Assert.Equal(Path.Combine(dataDirectory, "History", "Sqlite"), resolved);
    }
}
