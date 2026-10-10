using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Tests;

public class StoragePathRegistryTests
{
    [Fact]
    public void WhenHashIsNewOrChanged_ThenTryUpdateHashRecordsIt()
    {
        // Arrange
        var registry = new StoragePathRegistry();

        // Act
        var recordedFirst = registry.TryUpdateHash("Motor.json", "A");
        var recordedChange = registry.TryUpdateHash("/motor.json", "B");

        // Assert
        Assert.True(recordedFirst);
        Assert.True(recordedChange);
        Assert.False(registry.HasHashChanged("Motor.json", "B"));
    }

    [Fact]
    public void WhenHashIsAlreadyRecorded_ThenTryUpdateHashReturnsFalse()
    {
        // Arrange
        var registry = new StoragePathRegistry();
        registry.UpdateHash("Motor.json", "A");

        // Act
        var recorded = registry.TryUpdateHash("Motor.json", "A");

        // Assert
        Assert.False(recorded);
    }

    [Fact]
    public void WhenSameHashIsReportedConcurrently_ThenExactlyOneCallerRecordsIt()
    {
        // Arrange
        var registry = new StoragePathRegistry();
        registry.UpdateHash("Motor.json", "A");
        var recordedCount = 0;

        // Act
        Parallel.For(0, 64, _ =>
        {
            if (registry.TryUpdateHash("Motor.json", "B"))
            {
                Interlocked.Increment(ref recordedCount);
            }
        });

        // Assert
        Assert.Equal(1, recordedCount);
    }
}
