using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Tests;

public class StorageCallTimeoutTests
{
    [Fact]
    public async Task WhenStreamReadExceedsTheLimit_ThenPlainTimeoutNamesWhatWasDoneAndThePath()
    {
        // Arrange
        var timeProvider = new ManualTimeProvider();
        var read = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var limited = read.Task.WithStorageTimeoutAsync("hashing", "Docs/Notes.md", timeProvider, CancellationToken.None);

        // Act
        timeProvider.Advance(StorageCallTimeout.Limit);
        var exception = await Assert.ThrowsAsync<TimeoutException>(() => limited);

        // Assert
        Assert.Contains("hashing", exception.Message);
        Assert.Contains("Docs/Notes.md", exception.Message);
    }
}
