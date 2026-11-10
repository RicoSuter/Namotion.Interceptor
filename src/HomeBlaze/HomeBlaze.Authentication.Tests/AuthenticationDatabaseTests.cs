using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HomeBlaze.Authentication.Tests;

public class AuthenticationDatabaseTests
{
    [Fact]
    public async Task WhenTheDatabaseFileDoesNotExist_ThenInitializeCreatesItAndIsAvailable()
    {
        // Arrange
        using var testDatabase = new TestDatabase();
        var database = new AuthenticationDatabase(testDatabase.Path, testDatabase.CreateContext, NullLogger<AuthenticationDatabase>.Instance);

        // Act
        await database.InitializeAsync(CancellationToken.None);

        // Assert
        Assert.True(database.IsAvailable);
        Assert.True(File.Exists(testDatabase.Path));
    }

    [Fact]
    public void WhenTheModelIsComparedWithTheMigrations_ThenNoChangeLacksAMigration()
    {
        // Arrange
        using var testDatabase = new TestDatabase();
        using var context = testDatabase.CreateContext();

        // Act
        var hasPendingModelChanges = context.Database.HasPendingModelChanges();

        // Assert
        Assert.False(hasPendingModelChanges);
    }

    [Fact]
    public async Task WhenTheDatabaseIsMarkedUnavailableBeforeItIsInitialized_ThenItStaysUnavailable()
    {
        // Arrange
        using var testDatabase = new TestDatabase();
        var database = new AuthenticationDatabase(testDatabase.Path, testDatabase.CreateContext, NullLogger<AuthenticationDatabase>.Instance);
        database.MarkUnavailable("The seed marker exists but no user does.");

        // Act
        await database.InitializeAsync(CancellationToken.None);

        // Assert
        Assert.False(database.IsAvailable);
        Assert.Equal("The seed marker exists but no user does.", database.UnavailableReason);
    }

    [Fact]
    public async Task WhenTheDatabaseIsMarkedUnavailableAfterItIsInitialized_ThenAnotherInitializeKeepsItUnavailable()
    {
        // Arrange
        using var testDatabase = new TestDatabase();
        var database = new AuthenticationDatabase(testDatabase.Path, testDatabase.CreateContext, NullLogger<AuthenticationDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        database.MarkUnavailable("The seed marker exists but no user does.");

        // Act
        await database.InitializeAsync(CancellationToken.None);

        // Assert
        Assert.False(database.IsAvailable);
        Assert.Equal("The seed marker exists but no user does.", database.UnavailableReason);
    }

    [Fact]
    public async Task WhenTheDatabaseCannotBeOpened_ThenItIsUnavailableWithAReason()
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = directory.FullName; // a directory, not a file: SQLite cannot open it
            var database = new AuthenticationDatabase(path, () => TestDatabase.CreateContext(path), NullLogger<AuthenticationDatabase>.Instance);

            // Act
            await database.InitializeAsync(CancellationToken.None);

            // Assert
            Assert.False(database.IsAvailable);
            Assert.NotNull(database.UnavailableReason);
        }
        finally
        {
            TestDatabase.ClearPool(directory.FullName);
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task WhenTheDatabaseIsInitialized_ThenItIsNotReadyUntilMarkedReady()
    {
        // Arrange
        using var testDatabase = new TestDatabase();
        var database = new AuthenticationDatabase(testDatabase.Path, testDatabase.CreateContext, NullLogger<AuthenticationDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        var isReadyBefore = database.IsReady;

        // Act
        database.MarkReady();

        // Assert
        Assert.False(isReadyBefore);
        Assert.True(database.IsReady);
    }

    [Fact]
    public void WhenTheDatabaseIsMarkedReadyBeforeItIsInitialized_ThenItIsNotReady()
    {
        // Arrange
        using var testDatabase = new TestDatabase();
        var database = new AuthenticationDatabase(testDatabase.Path, testDatabase.CreateContext, NullLogger<AuthenticationDatabase>.Instance);

        // Act
        database.MarkReady();

        // Assert
        Assert.False(database.IsReady);
    }

    [Fact]
    public async Task WhenTheDatabaseIsMarkedUnavailable_ThenMarkingItReadyDoesNotReopenIt()
    {
        // Arrange
        using var testDatabase = new TestDatabase();
        var database = new AuthenticationDatabase(testDatabase.Path, testDatabase.CreateContext, NullLogger<AuthenticationDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        database.MarkUnavailable("The seed marker exists but no user does.");

        // Act
        database.MarkReady();

        // Assert
        Assert.False(database.IsReady);
        Assert.False(database.IsAvailable);
    }
}
