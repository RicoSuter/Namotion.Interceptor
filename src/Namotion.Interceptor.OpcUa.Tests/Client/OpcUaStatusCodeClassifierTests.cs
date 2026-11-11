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
    [InlineData(nameof(StatusCodes.BadNodeIdUnknown))]
    [InlineData(nameof(StatusCodes.BadNodeIdInvalid))]
    [InlineData(nameof(StatusCodes.BadAttributeIdInvalid))]
    [InlineData(nameof(StatusCodes.BadIndexRangeInvalid))]
    [InlineData(nameof(StatusCodes.BadTypeMismatch))]
    [InlineData(nameof(StatusCodes.BadSecurityModeInsufficient))]
    [InlineData(nameof(StatusCodes.BadNotWritable))]
    [InlineData(nameof(StatusCodes.BadWriteNotSupported))]
    public void WhenStatusIsPermanentBadCode_ThenIsTransientErrorReturnsFalse(string statusCodeName)
    {
        // Arrange
        var status = StatusCodeNames.Resolve(statusCodeName);

        // Act
        var isTransient = OpcUaStatusCodeClassifier.IsTransientError(status);

        // Assert
        Assert.False(isTransient);
    }

    [Theory]
    [InlineData(nameof(StatusCodes.BadTimeout))]
    [InlineData(nameof(StatusCodes.BadCommunicationError))]
    [InlineData(nameof(StatusCodes.BadServerNotConnected))]
    [InlineData(nameof(StatusCodes.BadServerHalted))]
    [InlineData(nameof(StatusCodes.BadShutdown))]
    [InlineData(nameof(StatusCodes.BadResourceUnavailable))]
    [InlineData(nameof(StatusCodes.BadOutOfMemory))]
    [InlineData(nameof(StatusCodes.BadOutOfService))]
    [InlineData(nameof(StatusCodes.BadTooManyOperations))]
    [InlineData(nameof(StatusCodes.BadSessionIdInvalid))]
    [InlineData(nameof(StatusCodes.BadSecureChannelClosed))]
    [InlineData(nameof(StatusCodes.BadDeviceFailure))]
    [InlineData(nameof(StatusCodes.BadSensorFailure))]
    [InlineData(nameof(StatusCodes.BadTooManyMonitoredItems))]
    public void WhenStatusIsTransientBadCode_ThenIsTransientErrorReturnsTrue(string statusCodeName)
    {
        // Arrange
        var status = StatusCodeNames.Resolve(statusCodeName);

        // Act
        var isTransient = OpcUaStatusCodeClassifier.IsTransientError(status);

        // Assert
        Assert.True(isTransient);
    }

    [Theory]
    // Role permissions and the AccessLevel attribute are mutable server-side, so these can start
    // succeeding mid-session. Classifying them permanent would drop the monitored item and forfeit
    // both in-session recovery routes, leaving the property dark until the next reconnect.
    [InlineData(nameof(StatusCodes.BadUserAccessDenied))]
    [InlineData(nameof(StatusCodes.BadNotReadable))]
    [InlineData(nameof(StatusCodes.BadNotImplemented))]
    public void WhenStatusIsAccessScoped_ThenIsTransientErrorReturnsTrue(string statusCodeName)
    {
        // Arrange
        var status = StatusCodeNames.Resolve(statusCodeName);

        // Act
        var isTransient = OpcUaStatusCodeClassifier.IsTransientError(status);

        // Assert
        Assert.True(isTransient);
    }

    [Fact]
    public void WhenStatusIsGood_ThenIsTransientErrorReturnsFalse()
    {
        // Arrange
        var status = StatusCodes.Good;

        // Act & Assert
        Assert.False(OpcUaStatusCodeClassifier.IsTransientError(status));
    }

    [Fact]
    public void WhenStatusIsUncertain_ThenIsTransientErrorReturnsFalse()
    {
        // Arrange: Uncertain carries a real value with reduced confidence; it is neither
        // a transient failure (no retry will improve it) nor a permanent design-time error.
        var status = StatusCodes.Uncertain;

        // Act & Assert
        Assert.False(OpcUaStatusCodeClassifier.IsTransientError(status));
    }
}
