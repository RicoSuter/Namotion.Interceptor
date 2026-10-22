namespace HomeBlaze.App.Tests;

public class WorkspaceLockTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("workspace-lock").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void WhenNoLockIsHeld_ThenItIsAcquired()
    {
        // Act
        var acquired = WorkspaceLock.TryAcquire(_directory, out var workspaceLock);

        // Assert
        Assert.True(acquired);
        Assert.NotNull(workspaceLock);
        workspaceLock.Dispose();
    }

    [Fact]
    public void WhenALockIsAlreadyHeld_ThenASecondAcquireFails()
    {
        // Arrange
        Assert.True(WorkspaceLock.TryAcquire(_directory, out var first));

        // Act
        var acquired = WorkspaceLock.TryAcquire(_directory, out var second);

        // Assert
        Assert.False(acquired);
        Assert.Null(second);
        first!.Dispose();
    }

    [Fact]
    public void WhenALockIsReleased_ThenItCanBeAcquiredAgain()
    {
        // Arrange
        Assert.True(WorkspaceLock.TryAcquire(_directory, out var first));
        first!.Dispose();

        // Act
        var acquired = WorkspaceLock.TryAcquire(_directory, out var second);

        // Assert
        Assert.True(acquired);
        second!.Dispose();
    }

    [Fact]
    public void WhenTheDirectoryDoesNotExist_ThenItIsCreated()
    {
        // Arrange
        var nested = Path.Combine(_directory, "created", "here");

        // Act
        var acquired = WorkspaceLock.TryAcquire(nested, out var workspaceLock);

        // Assert
        Assert.True(acquired);
        Assert.True(Directory.Exists(nested));
        workspaceLock!.Dispose();
    }
}
