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

    [Theory]
    [InlineData("82:00:00:00:00:01", true)]
    [InlineData("32:00:00:00:00:02", true)]
    [InlineData("d6-00-00-00-00-03", true)]
    [InlineData("FE:00:00:00:00:04", true)]
    [InlineData("00:00:5E:00:53:01", false)]
    [InlineData("3C:00:00:00:00:05", false)]
    [InlineData("01:00:5E:00:00:01", false)]
    [InlineData("not a mac", false)]
    [InlineData(null, false)]
    public void WhenAnAddressIsChecked_ThenOnlyTheLocallyAdministeredBitMakesItLocallyAdministered(string? input, bool expected)
    {
        // Act
        var isLocallyAdministered = MacAddresses.IsLocallyAdministered(input);

        // Assert
        Assert.Equal(expected, isLocallyAdministered);
    }
}
