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
}
