using HomeBlaze.AI.Mcp;
using Xunit;

namespace HomeBlaze.AI.Tests.Mcp;

public class MethodResultLimitTests
{
    [Fact]
    public void WhenAStringIsAtTheLimit_ThenItIsReturnedUnchanged()
    {
        // Arrange
        var text = new string('a', MethodResultLimit.MaxStringLength);

        // Act
        var result = MethodResultLimit.Apply(text);

        // Assert
        Assert.Same(text, result);
    }

    [Fact]
    public void WhenTheCutWouldSplitASurrogatePair_ThenThePairIsCutAsAWhole()
    {
        // Arrange
        var text = new string('a', MethodResultLimit.MaxStringLength - 1) + "\U0001F600" + "b";

        // Act
        var result = Assert.IsType<string>(MethodResultLimit.Apply(text));

        // Assert
        Assert.Equal(new string('a', MethodResultLimit.MaxStringLength - 1) + "\n[truncated: 3 characters]", result);
    }

    [Fact]
    public void WhenTheResultIsNotAString_ThenItIsReturnedUnchanged()
    {
        // Arrange
        var value = new[] { 1, 2, 3 };

        // Act
        var result = MethodResultLimit.Apply(value);

        // Assert
        Assert.Same(value, result);
    }
}
