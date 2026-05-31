using Namotion.Interceptor.OpcUa.Client;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Tests for the shared OPC UA status code classifier. The permanent list is unified
/// across browse / read / write / subscribe operations, so a single classifier covers
/// every callsite. Verifies that the classifier treats codes consistently regardless
/// of the operation context they appear in.
/// </summary>
public class OpcUaStatusCodeClassifierTests
{
    [Theory]
    [InlineData(StatusCodes.BadNodeIdUnknown)]
    [InlineData(StatusCodes.BadNodeIdInvalid)]
    [InlineData(StatusCodes.BadAttributeIdInvalid)]
    [InlineData(StatusCodes.BadIndexRangeInvalid)]
    [InlineData(StatusCodes.BadTypeMismatch)]
    [InlineData(StatusCodes.BadUserAccessDenied)]
    [InlineData(StatusCodes.BadSecurityModeInsufficient)]
    [InlineData(StatusCodes.BadNotImplemented)]
    [InlineData(StatusCodes.BadNotReadable)]
    [InlineData(StatusCodes.BadNotWritable)]
    [InlineData(StatusCodes.BadWriteNotSupported)]
    public void WhenStatusIsPermanentBadCode_ThenIsTransientReturnsFalseAndIsPermanentReturnsTrue(uint statusCode)
    {
        // Arrange
        var status = new StatusCode(statusCode);

        // Act
        var isTransient = OpcUaStatusCodeClassifier.IsTransientError(status);
        var isPermanent = OpcUaStatusCodeClassifier.IsPermanentError(status);

        // Assert
        Assert.False(isTransient);
        Assert.True(isPermanent);
    }

    [Theory]
    [InlineData(StatusCodes.BadTimeout)]
    [InlineData(StatusCodes.BadCommunicationError)]
    [InlineData(StatusCodes.BadServerNotConnected)]
    [InlineData(StatusCodes.BadServerHalted)]
    [InlineData(StatusCodes.BadShutdown)]
    [InlineData(StatusCodes.BadResourceUnavailable)]
    [InlineData(StatusCodes.BadOutOfMemory)]
    [InlineData(StatusCodes.BadOutOfService)]
    [InlineData(StatusCodes.BadTooManyOperations)]
    [InlineData(StatusCodes.BadSessionIdInvalid)]
    [InlineData(StatusCodes.BadSecureChannelClosed)]
    [InlineData(StatusCodes.BadDeviceFailure)]
    [InlineData(StatusCodes.BadSensorFailure)]
    [InlineData(StatusCodes.BadTooManyMonitoredItems)]
    public void WhenStatusIsTransientBadCode_ThenIsTransientReturnsTrueAndIsPermanentReturnsFalse(uint statusCode)
    {
        // Arrange
        var status = new StatusCode(statusCode);

        // Act
        var isTransient = OpcUaStatusCodeClassifier.IsTransientError(status);
        var isPermanent = OpcUaStatusCodeClassifier.IsPermanentError(status);

        // Assert
        Assert.True(isTransient);
        Assert.False(isPermanent);
    }

    [Fact]
    public void WhenStatusIsGood_ThenBothPredicatesReturnFalse()
    {
        // Arrange
        var status = new StatusCode(StatusCodes.Good);

        // Act & Assert
        Assert.False(OpcUaStatusCodeClassifier.IsTransientError(status));
        Assert.False(OpcUaStatusCodeClassifier.IsPermanentError(status));
    }

    [Fact]
    public void WhenStatusIsUncertain_ThenBothPredicatesReturnFalse()
    {
        // Arrange: Uncertain carries a real value with reduced confidence; it is neither
        // a transient failure (no retry will improve it) nor a permanent design-time error.
        var status = new StatusCode(StatusCodes.Uncertain);

        // Act & Assert
        Assert.False(OpcUaStatusCodeClassifier.IsTransientError(status));
        Assert.False(OpcUaStatusCodeClassifier.IsPermanentError(status));
    }
}
