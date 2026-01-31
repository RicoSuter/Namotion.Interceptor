using Namotion.Interceptor.OpcUa.Client.Graph;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Tests for write error classification logic.
/// Verifies that transient vs permanent write errors are correctly classified for retry decisions.
/// </summary>
public class WriteErrorClassificationTests
{
    [Theory]
    [InlineData(StatusCodes.BadNodeIdUnknown, false)]      // Node doesn't exist - permanent
    [InlineData(StatusCodes.BadAttributeIdInvalid, false)] // Wrong attribute - permanent
    [InlineData(StatusCodes.BadTypeMismatch, false)]       // Wrong type - permanent
    [InlineData(StatusCodes.BadWriteNotSupported, false)]  // Read-only - permanent
    [InlineData(StatusCodes.BadUserAccessDenied, false)]   // No permission - permanent
    [InlineData(StatusCodes.BadNotWritable, false)]        // Read-only - permanent
    public void IsTransientWriteError_WithPermanentError_ReturnsFalse(uint statusCode, bool expected)
    {
        // Arrange
        var status = new StatusCode(statusCode);

        // Act
        var result = PropertyWriter.IsTransientWriteError(status);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(StatusCodes.BadTimeout, true)]              // Timeout - transient
    [InlineData(StatusCodes.BadServerNotConnected, true)]   // Connection lost - transient
    [InlineData(StatusCodes.BadOutOfService, true)]         // Server busy - transient
    [InlineData(StatusCodes.BadTooManyOperations, true)]    // Server overloaded - transient
    [InlineData(StatusCodes.BadSessionIdInvalid, true)]     // Session expired - transient
    [InlineData(StatusCodes.BadSecureChannelClosed, true)]  // Channel closed - transient
    public void IsTransientWriteError_WithTransientError_ReturnsTrue(uint statusCode, bool expected)
    {
        // Arrange
        var status = new StatusCode(statusCode);

        // Act
        var result = PropertyWriter.IsTransientWriteError(status);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsTransientWriteError_WithGoodStatus_ReturnsFalse()
    {
        // Arrange
        var status = new StatusCode(StatusCodes.Good);

        // Act
        var result = PropertyWriter.IsTransientWriteError(status);

        // Assert
        Assert.False(result); // Good is not a "bad" status, so IsBad returns false
    }

    [Fact]
    public void IsTransientWriteError_WithUncertain_ReturnsFalse()
    {
        // Arrange
        var status = new StatusCode(StatusCodes.Uncertain);

        // Act
        var result = PropertyWriter.IsTransientWriteError(status);

        // Assert
        Assert.False(result); // Uncertain is not "bad"
    }
}
