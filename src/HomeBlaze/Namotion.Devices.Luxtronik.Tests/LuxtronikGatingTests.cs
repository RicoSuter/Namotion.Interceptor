namespace Namotion.Devices.Luxtronik.Tests;

public class LuxtronikGatingTests
{
    [Theory]
    [InlineData(null, 3, 90, 1, true)]
    [InlineData("3.92.0", 3, 90, 1, false)]
    [InlineData("3.92.0", 3, 92, 0, true)]
    [InlineData("3.92.1", 3, 92, 0, false)]
    [InlineData("3.92.0", 3, 92, 3, true)]
    public void WhenCheckingFirmware_ThenMinimumVersionIsEnforced(string? minimumFirmware, int major, int minor, int patch, bool expected)
    {
        // Act
        var isSupported = LuxtronikGating.IsSupported(minimumFirmware, LuxtronikFeature.None, new Version(major, minor, patch), configuredFeatures: null);

        // Assert
        Assert.Equal(expected, isSupported);
    }

    [Fact]
    public void WhenFeatureIsNotConfigured_ThenItIsNotSupported()
    {
        // Arrange
        var configuredFeatures = new HashSet<LuxtronikFeature> { LuxtronikFeature.Heating };

        // Act
        var isSupported = LuxtronikGating.IsSupported(null, LuxtronikFeature.Pool, new Version(3, 92, 3), configuredFeatures);

        // Assert
        Assert.False(isSupported);
    }

    [Fact]
    public void WhenFeatureFlagsAreUnknown_ThenFeatureGatesAreIgnored()
    {
        // Act
        var isSupported = LuxtronikGating.IsSupported(null, LuxtronikFeature.Pool, new Version(3, 92, 3), configuredFeatures: null);

        // Assert
        Assert.True(isSupported);
    }

    [Fact]
    public void WhenReadingFeatureFlags_ThenSetBitsBecomeConfiguredFeatures()
    {
        // Arrange
        var flags = new bool[12];
        flags[0] = true;
        flags[2] = true;
        flags[6] = true;

        // Act
        var features = LuxtronikGating.GetConfiguredFeatures(flags);

        // Assert
        Assert.Equal(
            new[] { LuxtronikFeature.Heating, LuxtronikFeature.Cooling, LuxtronikFeature.MixingCircuit1Heating },
            features.OrderBy(feature => feature));
    }
}
