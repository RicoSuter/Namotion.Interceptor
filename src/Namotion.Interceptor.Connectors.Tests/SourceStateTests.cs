namespace Namotion.Interceptor.Connectors.Tests;

public class SourceStateTests
{
    [Fact]
    public void WhenReadingTheEnum_ThenUnclaimedIsTheDefault()
    {
        // Arrange & Act
        var state = default(SourceState);

        // Assert
        Assert.Equal(SourceState.Unclaimed, state);
    }
}
