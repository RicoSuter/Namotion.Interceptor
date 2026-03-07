using Namotion.Interceptor.Connectors.TwinCAT.Client;
using TwinCAT.Ads;
using Xunit;

namespace Namotion.Interceptor.Connectors.TwinCAT.Tests.Client;

public class AdsErrorClassifierTests
{
    [Theory]
    [InlineData(AdsErrorCode.DeviceSymbolNotFound)]
    [InlineData(AdsErrorCode.DeviceInvalidSize)]
    [InlineData(AdsErrorCode.DeviceInvalidData)]
    [InlineData(AdsErrorCode.DeviceServiceNotSupported)]
    [InlineData(AdsErrorCode.DeviceInvalidAccess)]
    [InlineData(AdsErrorCode.DeviceInvalidOffset)]
    public void IsTransientError_WithPermanentError_ReturnsFalse(AdsErrorCode errorCode)
    {
        // Arrange & Act
        var result = AdsErrorClassifier.IsTransientError(errorCode);

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData(AdsErrorCode.TargetPortNotFound)]
    [InlineData(AdsErrorCode.TargetMachineNotFound)]
    [InlineData(AdsErrorCode.ClientPortNotOpen)]
    [InlineData(AdsErrorCode.DeviceError)]
    [InlineData(AdsErrorCode.DeviceTimeOut)]
    [InlineData(AdsErrorCode.DeviceBusy)]
    public void IsTransientError_WithTransientError_ReturnsTrue(AdsErrorCode errorCode)
    {
        // Arrange & Act
        var result = AdsErrorClassifier.IsTransientError(errorCode);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsTransientError_WithUnknownErrorCode_ReturnsTrue()
    {
        // Arrange
        var unknownCode = (AdsErrorCode)99999;

        // Act
        var result = AdsErrorClassifier.IsTransientError(unknownCode);

        // Assert - unknown codes are treated as transient (safer)
        Assert.True(result);
    }

    [Fact]
    public void IsTransientError_WithNoError_FallsThroughToDefaultTrue()
    {
        // Arrange
        var errorCode = AdsErrorCode.NoError;

        // Act
        var result = AdsErrorClassifier.IsTransientError(errorCode);

        // Assert - NoError falls through to default case which returns true (not classified as permanent)
        Assert.True(result);
    }
}
