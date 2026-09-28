using Namotion.Interceptor.OpcUa.Client;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Tests for the shared OPC UA status code classifier. One permanent list backs every
/// callsite, so a single classifier covers the write and subscription-health paths.
/// Verifies that the classifier treats codes consistently regardless of the operation
/// context they appear in.
/// </summary>
public class OpcUaStatusCodeClassifierTests
{
    [Theory]
    [InlineData(StatusCodes.BadNodeIdUnknown)]
    [InlineData(StatusCodes.BadNodeIdInvalid)]
    [InlineData(StatusCodes.BadAttributeIdInvalid)]
    [InlineData(StatusCodes.BadIndexRangeInvalid)]
    [InlineData(StatusCodes.BadTypeMismatch)]
    [InlineData(StatusCodes.BadSecurityModeInsufficient)]
    [InlineData(StatusCodes.BadNotWritable)]
    [InlineData(StatusCodes.BadWriteNotSupported)]
    public void WhenStatusIsPermanentBadCode_ThenIsTransientErrorReturnsFalse(uint statusCode)
    {
        // Arrange
        var status = new StatusCode(statusCode);

        // Act
        var isTransient = OpcUaStatusCodeClassifier.IsTransientError(status);

        // Assert
        Assert.False(isTransient);
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
    public void WhenStatusIsTransientBadCode_ThenIsTransientErrorReturnsTrue(uint statusCode)
    {
        // Arrange
        var status = new StatusCode(statusCode);

        // Act
        var isTransient = OpcUaStatusCodeClassifier.IsTransientError(status);

        // Assert
        Assert.True(isTransient);
    }

    [Theory]
    // Role permissions and the AccessLevel attribute are mutable server-side, so these can start
    // succeeding mid-session. Classifying them permanent would drop the monitored item and forfeit
    // both in-session recovery routes, leaving the property dark until the next reconnect.
    [InlineData(StatusCodes.BadUserAccessDenied)]
    [InlineData(StatusCodes.BadNotReadable)]
    [InlineData(StatusCodes.BadNotImplemented)]
    public void WhenStatusIsAccessScoped_ThenIsTransientErrorReturnsTrue(uint statusCode)
    {
        // Arrange
        var status = new StatusCode(statusCode);

        // Act
        var isTransient = OpcUaStatusCodeClassifier.IsTransientError(status);

        // Assert
        Assert.True(isTransient);
    }

    [Fact]
    public void WhenStatusIsGood_ThenIsTransientErrorReturnsFalse()
    {
        // Arrange
        var status = new StatusCode(StatusCodes.Good);

        // Act & Assert
        Assert.False(OpcUaStatusCodeClassifier.IsTransientError(status));
    }

    [Fact]
    public void WhenStatusIsUncertain_ThenIsTransientErrorReturnsFalse()
    {
        // Arrange: Uncertain carries a real value with reduced confidence; it is neither
        // a transient failure (no retry will improve it) nor a permanent design-time error.
        var status = new StatusCode(StatusCodes.Uncertain);

        // Act & Assert
        Assert.False(OpcUaStatusCodeClassifier.IsTransientError(status));
    }
}
