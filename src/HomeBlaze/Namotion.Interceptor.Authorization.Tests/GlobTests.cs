using Namotion.Interceptor.Authorization.Internal;

namespace Namotion.Interceptor.Authorization.Tests;

public class GlobTests
{
    [Theory]
    [InlineData("Brightness", "Brightness", true)]
    [InlineData("Brightness", "brightness", false)]
    [InlineData("*", "", true)]
    [InlineData("*", "Anything", true)]
    [InlineData("Set*", "SetPoint", true)]
    [InlineData("Set*", "Reset", false)]
    [InlineData("*Point", "SetPoint", true)]
    [InlineData("S*P*t", "SetPoint", true)]
    [InlineData("S*P*t", "SetPoints", false)]
    [InlineData("a*ab", "aaab", true)]
    [InlineData("a*b*", "ab", true)]
    [InlineData("a*", "", false)]
    [InlineData("**", "x", true)]
    [InlineData("Temperature@*", "Temperature@Unit", true)]
    [InlineData("Temperature", "Temperature@Unit", false)]
    [InlineData("", "", true)]
    [InlineData("", "a", false)]
    public void WhenPatternIsMatchedAgainstText_ThenResultIsAsExpected(string pattern, string text, bool expected)
    {
        // Act
        var result = Glob.IsMatch(pattern, text);

        // Assert
        Assert.Equal(expected, result);
    }
}
