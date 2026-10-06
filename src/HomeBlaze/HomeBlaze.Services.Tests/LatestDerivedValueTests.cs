namespace HomeBlaze.Services.Tests;

public class LatestDerivedValueTests
{
    [Fact]
    public void WhenEarlierEvaluationStoresAfterLaterOne_ThenLaterValueIsKept()
    {
        // Arrange
        var latestValue = new LatestDerivedValue<string>();
        var earlierEvaluation = latestValue.BeginEvaluation();
        var laterEvaluation = latestValue.BeginEvaluation();

        // Act
        latestValue.Store(laterEvaluation, "later");
        var returnedValue = latestValue.Store(earlierEvaluation, "earlier");

        // Assert
        Assert.Equal("earlier", returnedValue);
        Assert.True(latestValue.TryGetValue(out var value));
        Assert.Equal("later", value);
    }

    [Fact]
    public void WhenNothingIsStored_ThenTryGetValueReturnsFalse()
    {
        // Arrange
        var latestValue = new LatestDerivedValue<string>();
        latestValue.BeginEvaluation();

        // Act
        var hasValue = latestValue.TryGetValue(out var value);

        // Assert
        Assert.False(hasValue);
        Assert.Null(value);
    }
}
