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
    [InlineData("82:A6:88:A1:34:13", true)]
    [InlineData("32:2E:F0:0B:D8:9C", true)]
    [InlineData("d6-7b-bc-7c-7f-75", true)]
    [InlineData("FE:66:09:38:97:A3", true)]
    [InlineData("00:04:4B:A9:B7:3C", false)]
    [InlineData("3C:8A:1F:32:E1:57", false)]
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
