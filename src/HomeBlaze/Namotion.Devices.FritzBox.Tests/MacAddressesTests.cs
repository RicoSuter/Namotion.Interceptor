using Xunit;

namespace Namotion.Devices.FritzBox.Tests;

public class MacAddressesTests
{
    [Theory]
    [InlineData("aa:bb:cc:00:11:22", "AA:BB:CC:00:11:22")]
    [InlineData("  AA:BB:CC:00:11:22 ", "AA:BB:CC:00:11:22")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void WhenAnAddressIsNormalized_ThenItIsTrimmedAndUpperCaseOrNullWhenEmpty(string? input, string? expected)
    {
        // Act
        var normalized = MacAddresses.Normalize(input);

        // Assert
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("aa:bb:cc:00:11:22", "AABBCC001122")]
    [InlineData("AA-BB-CC-00-11-22", "AABBCC001122")]
    [InlineData("aabb.cc00.1122", "AABBCC001122")]
    [InlineData(" aabbcc001122 ", "AABBCC001122")]
    [InlineData("AA:BB:CC:00:11", null)]
    [InlineData("AA:BB:CC:00:11:22:33", null)]
    [InlineData("GG:BB:CC:00:11:22", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void WhenAnAddressIsReducedToItsHexDigits_ThenAnyNotationGivesTheSameDigitsOrNullWhenInvalid(string? input, string? expected)
    {
        // Act
        var digits = MacAddresses.ToHexDigits(input);

        // Assert
        Assert.Equal(expected, digits);
    }
}
