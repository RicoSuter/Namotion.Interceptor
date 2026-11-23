using HomeBlaze.Abstractions.Networking;
using Xunit;

namespace HomeBlaze.Abstractions.Tests;

public class MacAddressComparisonTests
{
    [Theory]
    [InlineData("3C:8A:1F:32:E1:57", "3C:8A:1F:32:E1:57")]
    [InlineData("3c:8a:1f:32:e1:57", "3C8A1F32E157")]
    [InlineData("3C-8A-1F-32-E1-57", "3c8a1f32e157")]
    public void WhenTheAddressesDifferOnlyInCaseAndSeparators_ThenTheyAreEqual(string macAddress, string otherMacAddress)
    {
        // Act
        var areEqual = MacAddressComparison.AreEqual(macAddress, otherMacAddress);

        // Assert
        Assert.True(areEqual);
    }

    [Theory]
    [InlineData("3C:8A:1F:32:E1:57", "3C:8A:1F:32:E1:58")]
    [InlineData("3C:8A:1F:32:E1:57", "3C:8A:1F:32:E1")]
    [InlineData(null, "3C8A1F32E157")]
    [InlineData("3C8A1F32E157", null)]
    [InlineData(" ", "3C8A1F32E157")]
    public void WhenTheAddressesDifferOrOneIsMissing_ThenTheyAreNotEqual(string? macAddress, string? otherMacAddress)
    {
        // Act
        var areEqual = MacAddressComparison.AreEqual(macAddress, otherMacAddress);

        // Assert
        Assert.False(areEqual);
    }
}
