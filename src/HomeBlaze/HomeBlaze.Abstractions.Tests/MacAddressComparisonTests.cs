using HomeBlaze.Abstractions.Networking;
using Xunit;

namespace HomeBlaze.Abstractions.Tests;

public class MacAddressComparisonTests
{
    [Theory]
    [InlineData("02:AB:CD:00:00:01", "02:AB:CD:00:00:01")]
    [InlineData("02:ab:cd:00:00:01", "02ABCD000001")]
    [InlineData("02-AB-CD-00-00-01", "02abcd000001")]
    public void WhenTheAddressesDifferOnlyInCaseAndSeparators_ThenTheyAreEqual(string macAddress, string otherMacAddress)
    {
        // Act
        var areEqual = MacAddressComparison.AreEqual(macAddress, otherMacAddress);

        // Assert
        Assert.True(areEqual);
    }

    [Theory]
    [InlineData("02:AB:CD:00:00:01", "02:AB:CD:00:00:02")]
    [InlineData("02:AB:CD:00:00:01", "02:AB:CD:00:00")]
    [InlineData(null, "02ABCD000001")]
    [InlineData("02ABCD000001", null)]
    [InlineData(" ", "02ABCD000001")]
    [InlineData(":", "-")]
    [InlineData("::", "--")]
    public void WhenTheAddressesDifferOrOneIsMissing_ThenTheyAreNotEqual(string? macAddress, string? otherMacAddress)
    {
        // Act
        var areEqual = MacAddressComparison.AreEqual(macAddress, otherMacAddress);

        // Assert
        Assert.False(areEqual);
    }
}
