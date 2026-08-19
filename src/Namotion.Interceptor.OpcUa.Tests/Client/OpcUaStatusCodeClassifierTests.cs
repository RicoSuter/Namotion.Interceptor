using Namotion.Interceptor.OpcUa.Client;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Tests for the shared OPC UA status code classifier, which answers two questions off two lists:
/// whether a code is transient, which subscription health acts on, and whether a Write is refused for
/// the rest of the session, which the write path acts on. The lists differ because the dispositions
/// do; where they disagree is pinned here.
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
    public void WhenAPermanentCodeCarriesInfoBits_ThenIsTransientErrorReturnsFalse()
    {
        // Arrange: the low 16 bits describe the answer rather than name it, and a server is free to set
        // them. Matching on the whole 32-bit value would read this as a code the list does not hold.
        var status = new StatusCode(StatusCodes.BadNodeIdUnknown | 0x0403u);

        // Act & Assert
        Assert.False(OpcUaStatusCodeClassifier.IsTransientError(status));
    }

    [Fact]
    public void WhenStatusIsGood_ThenIsTransientErrorReturnsFalse()
    {
        // Arrange
        var status = new StatusCode(StatusCodes.Good);

        // Act & Assert
        Assert.False(OpcUaStatusCodeClassifier.IsTransientError(status));
    }

    [Theory]
    // Schema and type codes: permanent within a session by spec.
    [InlineData(StatusCodes.BadAttributeIdInvalid)]
    [InlineData(StatusCodes.BadTypeMismatch)]
    [InlineData(StatusCodes.BadWriteNotSupported)]
    // State-dependent codes: decided by address-space membership, role permissions and AccessLevel,
    // which a server keeps for a session but can also change mid-session; a reconnect re-attempts
    // everything either way.
    [InlineData(StatusCodes.BadNodeIdUnknown)]
    [InlineData(StatusCodes.BadUserAccessDenied)]
    [InlineData(StatusCodes.BadNotWritable)]
    // Request-decided codes: re-sending the identical change cannot change the answer.
    [InlineData(StatusCodes.BadNodeIdInvalid)]
    [InlineData(StatusCodes.BadIndexRangeInvalid)]
    // Channel-bound: answered per node from AccessRestrictions against the channel's security mode,
    // which cannot change without the reconnect that ends the hold.
    [InlineData(StatusCodes.BadSecurityModeInsufficient)]
    public void WhenAWriteIsRefusedForTheSession_ThenIsRefusedUntilReconnectReturnsTrue(uint statusCode)
    {
        // Arrange
        var status = new StatusCode(statusCode);

        // Act
        var isRefused = OpcUaStatusCodeClassifier.IsRefusedUntilReconnect(status);

        // Assert
        Assert.True(isRefused);
    }

    [Theory]
    [InlineData(StatusCodes.BadTimeout)]
    [InlineData(StatusCodes.BadCommunicationError)]
    [InlineData(StatusCodes.BadServerNotConnected)]
    [InlineData(StatusCodes.BadOutOfService)]
    [InlineData(StatusCodes.BadTooManyOperations)]
    [InlineData(StatusCodes.BadSessionIdInvalid)]
    [InlineData(StatusCodes.BadSecureChannelClosed)]
    [InlineData(StatusCodes.BadDeviceFailure)]
    public void WhenAWriteFailsTransiently_ThenIsRefusedUntilReconnectReturnsFalse(uint statusCode)
    {
        // Arrange
        var status = new StatusCode(statusCode);

        // Act
        var isRefused = OpcUaStatusCodeClassifier.IsRefusedUntilReconnect(status);

        // Assert
        Assert.False(isRefused);
    }

    [Theory]
    [InlineData(StatusCodes.Good)]
    [InlineData(StatusCodes.Uncertain)]
    public void WhenAWriteStatusIsNotBad_ThenIsRefusedUntilReconnectReturnsFalse(uint statusCode)
    {
        // Arrange
        var status = new StatusCode(statusCode);

        // Act
        var isRefused = OpcUaStatusCodeClassifier.IsRefusedUntilReconnect(status);

        // Assert
        Assert.False(isRefused);
    }

    [Fact]
    public void WhenARefusalCodeCarriesInfoBits_ThenIsRefusedUntilReconnectReturnsTrue()
    {
        // Arrange: the low 16 bits describe the answer rather than name it, and a server is free to set
        // them. Matching on the whole 32-bit value would read this as a code the list does not hold.
        var status = new StatusCode(StatusCodes.BadUserAccessDenied | 0x0403u);

        // Act & Assert
        Assert.True(OpcUaStatusCodeClassifier.IsRefusedUntilReconnect(status));
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
