using HomeBlaze.Game.World;

namespace HomeBlaze.Game.Tests.World;

public class SoundPathSearchTests
{
    [Theory]
    [InlineData(GameObject.MaximumSize + 1, 2)]
    [InlineData(2, 0)]
    public void WhenTheSourceHasAnInvalidSize_ThenNoTargetIsReached(int width, int depth)
    {
        // Arrange
        var search = new SoundPathSearch();
        var index = new GameWorldIndex();

        // Act
        search.Prepare(new Footprint(0, 0, width, depth), index);
        var length = search.GetLength(new Footprint(4, 0, 2, 2));

        // Assert
        Assert.Equal(SoundPathSearch.NoPath, length);
    }

    [Fact]
    public void WhenTheTargetHasAnInvalidSize_ThenItIsNotReached()
    {
        // Arrange
        var search = new SoundPathSearch();
        search.Prepare(new Footprint(0, 0, 2, 2), new GameWorldIndex());

        // Act
        var length = search.GetLength(new Footprint(int.MinValue, 0, int.MaxValue, 2));

        // Assert
        Assert.Equal(SoundPathSearch.NoPath, length);
    }

    [Fact]
    public void WhenNothingBlocks_ThenTheLengthIsTheOpenLength()
    {
        // Arrange
        var search = new SoundPathSearch();
        var source = new Footprint(0, 0, 2, 2);
        var target = new Footprint(13, 5, 2, 2);
        search.Prepare(source, new GameWorldIndex());

        // Act
        var length = search.GetLength(target);

        // Assert
        Assert.Equal(SoundPathSearch.GetOpenLength(source, target), length);
    }

    [Fact]
    public void WhenNoSourceIsPrepared_ThenGettingALengthThrows()
    {
        // Arrange
        var search = new SoundPathSearch();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => search.GetLength(new Footprint(0, 0, 2, 2)));
    }
}
