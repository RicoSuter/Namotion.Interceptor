using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Tests.World;

public class ChangedChunksTests
{
    [Fact]
    public void WhenTheSameChunkIsAddedTwice_ThenItCountsOnce()
    {
        // Arrange
        var changes = new ChangedChunks();

        // Act
        changes.Add((1, -2));
        changes.Add((1, -2));

        // Assert
        Assert.Equal(1, changes.Count);
        Assert.True(changes.Contains((1, -2)));
    }

    [Fact]
    public void WhenCleared_ThenItIsEmpty()
    {
        // Arrange
        var changes = new ChangedChunks();
        changes.Add((0, 0));
        changes.Add((3, 4));

        // Act
        changes.Clear();

        // Assert
        Assert.Equal(0, changes.Count);
        Assert.Empty(changes.Chunks);
        Assert.False(changes.Contains((0, 0)));
    }
}
