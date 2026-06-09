using Namotion.Interceptor.OpcUa.Client;
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
        var result = OpcUaSubjectClientSource.IsTransientWriteError(status);

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
        var result = OpcUaSubjectClientSource.IsTransientWriteError(status);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsTransientWriteError_WithGoodStatus_ReturnsFalse()
    {
        // Arrange
        var status = new StatusCode(StatusCodes.Good);

        // Act
        var result = OpcUaSubjectClientSource.IsTransientWriteError(status);

        // Assert
        Assert.False(result); // Good is not a "bad" status, so IsBad returns false
    }

    [Fact]
    public void IsTransientWriteError_WithUncertain_ReturnsFalse()
    {
        // Arrange
        var status = new StatusCode(StatusCodes.Uncertain);

        // Act
        var result = OpcUaSubjectClientSource.IsTransientWriteError(status);

        // Assert
        Assert.False(result); // Uncertain is not "bad"
    }

    [Theory]
    [InlineData(StatusCodes.BadNodeIdUnknown)]
    [InlineData(StatusCodes.BadAttributeIdInvalid)]
    [InlineData(StatusCodes.BadTypeMismatch)]
    [InlineData(StatusCodes.BadWriteNotSupported)]
    [InlineData(StatusCodes.BadUserAccessDenied)]
    [InlineData(StatusCodes.BadNotWritable)]
    public void IsPermanentWriteError_WithPermanentCode_ReturnsTrue(uint statusCode)
    {
        // Arrange
        var status = new StatusCode(statusCode);

        // Act
        var result = OpcUaSubjectClientSource.IsPermanentWriteError(status);

        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData(StatusCodes.BadTimeout)]
    [InlineData(StatusCodes.BadServerNotConnected)]
    [InlineData(StatusCodes.BadOutOfService)]
    [InlineData(StatusCodes.BadTooManyOperations)]
    [InlineData(StatusCodes.BadSessionIdInvalid)]
    [InlineData(StatusCodes.BadSecureChannelClosed)]
    public void IsPermanentWriteError_WithTransientCode_ReturnsFalse(uint statusCode)
    {
        // Arrange
        var status = new StatusCode(statusCode);

        // Act
        var result = OpcUaSubjectClientSource.IsPermanentWriteError(status);

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData(StatusCodes.Good)]
    [InlineData(StatusCodes.Uncertain)]
    public void IsPermanentWriteError_WithNonBadStatus_ReturnsFalse(uint statusCode)
    {
        // Arrange
        var status = new StatusCode(statusCode);

        // Act
        var result = OpcUaSubjectClientSource.IsPermanentWriteError(status);

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData(StatusCodes.BadTypeMismatch)]
    [InlineData(StatusCodes.BadUserAccessDenied)]
    [InlineData(StatusCodes.BadNotWritable)]
    public void ShouldDropFailedWrite_WhenConfigEnabledAndPermanentCode_ReturnsTrue(uint statusCode)
    {
        // Arrange
        var status = new StatusCode(statusCode);

        // Act
        var result = OpcUaSubjectClientSource.ShouldDropFailedWrite(status, dropPermanentWriteFailures: true);

        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData(StatusCodes.BadTypeMismatch)]
    [InlineData(StatusCodes.BadUserAccessDenied)]
    [InlineData(StatusCodes.BadNotWritable)]
    public void ShouldDropFailedWrite_WhenConfigDisabled_ReturnsFalseEvenForPermanentCode(uint statusCode)
    {
        // Arrange
        var status = new StatusCode(statusCode);

        // Act
        var result = OpcUaSubjectClientSource.ShouldDropFailedWrite(status, dropPermanentWriteFailures: false);

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData(StatusCodes.BadTimeout)]
    [InlineData(StatusCodes.BadServerNotConnected)]
    [InlineData(StatusCodes.Good)]
    [InlineData(StatusCodes.Uncertain)]
    public void ShouldDropFailedWrite_ForNonPermanentCode_ReturnsFalseRegardlessOfConfig(uint statusCode)
    {
        // Arrange
        var status = new StatusCode(statusCode);

        // Act
        var resultEnabled = OpcUaSubjectClientSource.ShouldDropFailedWrite(status, dropPermanentWriteFailures: true);
        var resultDisabled = OpcUaSubjectClientSource.ShouldDropFailedWrite(status, dropPermanentWriteFailures: false);

        // Assert
        Assert.False(resultEnabled);
        Assert.False(resultDisabled);
    }

    [Fact]
    public void DropPermanentWriteFailures_DefaultValue_IsTrue()
    {
        // Arrange
        var configuration = new OpcUaClientConfiguration { ServerUrl = "opc.tcp://localhost:4840" };

        // Act
        var actual = configuration.DropPermanentWriteFailures;

        // Assert
        Assert.True(actual);
    }
}
