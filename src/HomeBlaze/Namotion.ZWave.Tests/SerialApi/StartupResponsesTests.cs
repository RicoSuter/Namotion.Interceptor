using Namotion.ZWave.SerialApi;

namespace Namotion.ZWave.Tests.SerialApi;

public class StartupResponsesTests
{
    [Fact]
    public void WhenControllerVersionIsParsed_ThenLibraryVersionAndTypeAreRead()
    {
        // Act
        var (libraryVersion, libraryType) = StartupResponses.ParseControllerVersion([.. "Z-Wave 4.05"u8, 0x00, 0x01]);

        // Assert
        Assert.Equal("Z-Wave 4.05", libraryVersion);
        Assert.Equal(ZWaveLibraryType.StaticController, libraryType);
    }

    [Theory]
    [InlineData("5A2D5761766520372E313233" + "00" + "07")]
    [InlineData("5A2D5761766520372E313233" + "07")]
    public void WhenTheControllerVersionHasTwelveCharacters_ThenItIsReadWithOrWithoutTerminator(string payload)
    {
        // Act
        var (libraryVersion, libraryType) = StartupResponses.ParseControllerVersion(Convert.FromHexString(payload));

        // Assert
        Assert.Equal("Z-Wave 7.123", libraryVersion);
        Assert.Equal(ZWaveLibraryType.BridgeController, libraryType);
    }

    [Fact]
    public void WhenTheLibraryTypeIsZero_ThenItIsNotTakenForTheTerminator()
    {
        // Act
        var (libraryVersion, libraryType) = StartupResponses.ParseControllerVersion([.. "Z-Wave 4.05"u8, 0x00, 0x00]);

        // Assert
        Assert.Equal("Z-Wave 4.05", libraryVersion);
        Assert.Equal(ZWaveLibraryType.Unknown, libraryType);
    }

    [Fact]
    public void WhenControllerIdIsParsed_ThenHomeIdIsBigEndian()
    {
        // Act
        var (homeId, ownNodeId) = StartupResponses.ParseControllerId([0xC0, 0xFF, 0xEE, 0x01, 0x01]);

        // Assert
        Assert.Equal(0xC0FFEE01u, homeId);
        Assert.Equal(1, ownNodeId);
    }

    [Fact]
    public void WhenSerialApiCapabilitiesAreParsed_ThenIdsAndSupportedFunctionsAreRead()
    {
        // Arrange
        var bitmask = new byte[32];
        bitmask[(0x15 - 1) / 8] |= 1 << ((0x15 - 1) % 8);

        // Act
        var capabilities = StartupResponses.ParseSerialApiCapabilities([0x01, 0x02, 0x00, 0x86, 0x00, 0x01, 0x00, 0x5A, .. bitmask]);

        // Assert
        Assert.Equal("1.2", capabilities.Version);
        Assert.Equal(0x0086, capabilities.ManufacturerId);
        Assert.Equal(0x0001, capabilities.ProductType);
        Assert.Equal(0x005A, capabilities.ProductId);
        Assert.True(capabilities.SupportsFunction(0x15));
        Assert.False(capabilities.SupportsFunction(0x13));
    }

    [Fact]
    public void WhenInitDataIsParsed_ThenTheNodeBitmaskBecomesNodeIds()
    {
        // Arrange
        var bitmask = new byte[29];
        bitmask[0] = 0b0001_0011;
        bitmask[28] = 0b1000_0000;

        // Act
        var nodeIds = StartupResponses.ParseNodeIds([0x05, 0x08, 29, .. bitmask, 0x05, 0x00]);

        // Assert
        Assert.Equal(new byte[] { 1, 2, 5, 232 }, nodeIds);
    }

    [Fact]
    public void WhenControllerCapabilitiesAreParsed_ThenFlagsAreRead()
    {
        // Act
        var capabilities = StartupResponses.ParseControllerCapabilities([0x1C]);

        // Assert
        Assert.Equal(
            ZWaveControllerCapabilities.SisPresent | ZWaveControllerCapabilities.WasRealPrimary | ZWaveControllerCapabilities.Suc,
            capabilities);
    }

    [Fact]
    public void WhenAResponseIsTooShort_ThenParsingThrowsAZWaveException()
    {
        // Act & Assert
        Assert.Throws<ZWaveException>(() => StartupResponses.ParseControllerId([0xC0]));
        Assert.Throws<ZWaveException>(() => StartupResponses.ParseControllerVersion([0x01]));
        Assert.Throws<ZWaveException>(() => StartupResponses.ParseSerialApiCapabilities([0x01]));
        Assert.Throws<ZWaveException>(() => StartupResponses.ParseNodeIds([0x05, 0x08, 29, 0x01]));
        Assert.Throws<ZWaveException>(() => StartupResponses.ParseControllerCapabilities([]));
    }
}
