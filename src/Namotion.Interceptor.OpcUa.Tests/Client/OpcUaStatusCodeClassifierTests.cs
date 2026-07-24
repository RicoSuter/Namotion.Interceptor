using Namotion.Interceptor.OpcUa.Client;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Tests for the OPC UA status code classifier. It answers two questions that the access-scoped
/// codes answer oppositely: <see cref="OpcUaStatusCodeClassifier.IsTransientError"/> (can this
/// recover without a new session?) backs the subscribe and write paths, and
/// <see cref="OpcUaStatusCodeClassifier.ThrowIfTransientError"/> (would reloading now help?) backs
/// the browse and read paths, where access-scoped codes are skipped rather than retried.
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
    public void WhenStatusIsSessionPermanent_ThenIsTransientErrorReturnsFalse(uint statusCode)
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

    [Theory]
    [InlineData(StatusCodes.BadTimeout)]
    [InlineData(StatusCodes.BadOutOfService)]
    [InlineData(StatusCodes.BadTooManyMonitoredItems)]
    [InlineData(StatusCodes.BadServerNotConnected)]
    public void WhenBrowseStatusCanClearOnReload_ThenThrowIfTransientErrorThrows(uint statusCode)
    {
        // Arrange
        var status = new StatusCode(statusCode);

        // Act & Assert
        var exception = Assert.Throws<OpcUaTransientServiceException>(
            () => OpcUaStatusCodeClassifier.ThrowIfTransientError(status, "Browse", new NodeId(1)));
        Assert.Equal("Browse", exception.Operation);
        Assert.Equal(status, exception.StatusCode);
    }

    [Theory]
    // Permanent design-time codes: reloading repeats the failure, so the caller skips the node.
    [InlineData(StatusCodes.BadNodeIdUnknown)]
    [InlineData(StatusCodes.BadTypeMismatch)]
    [InlineData(StatusCodes.BadSecurityModeInsufficient)]
    // Access-scoped codes: transient over a longer horizon, but deterministic for this session, so
    // a browse/read reload would crash-loop. They must NOT throw here even though IsTransientError
    // classifies them transient for the subscribe path.
    [InlineData(StatusCodes.BadUserAccessDenied)]
    [InlineData(StatusCodes.BadNotReadable)]
    [InlineData(StatusCodes.BadNotImplemented)]
    // Non-bad statuses never throw.
    [InlineData(StatusCodes.Good)]
    [InlineData(StatusCodes.Uncertain)]
    public void WhenBrowseStatusWouldRepeatOnReload_ThenThrowIfTransientErrorDoesNotThrow(uint statusCode)
    {
        // Arrange
        var status = new StatusCode(statusCode);

        // Act & Assert (does not throw)
        OpcUaStatusCodeClassifier.ThrowIfTransientError(status, "Browse", new NodeId(1));
    }
}
