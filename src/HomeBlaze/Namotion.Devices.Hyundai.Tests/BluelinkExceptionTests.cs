using System.Net;
using Namotion.Devices.Hyundai.Client;

namespace Namotion.Devices.Hyundai.Tests;

public class BluelinkExceptionTests
{
    [Theory]
    [InlineData("7501", null)]
    [InlineData(null, "Key not authorized: token is expired")]
    [InlineData(null, "KEY NOT AUTHORIZED: token is expired")]
    [InlineData(null, "key not authorized: token is expired")]
    public void WhenCodeOrMessageSaysTheTokenExpired_ThenIsTokenExpired(string? code, string? message)
    {
        // Act
        var exception = new BluelinkApiException(HttpStatusCode.Forbidden, code, message);

        // Assert
        Assert.True(exception.IsTokenExpired);
    }

    [Fact]
    public void WhenStatusIsUnauthorizedWithoutCodeOrMessage_ThenIsTokenExpiredIsFalse()
    {
        // Act
        var exception = new BluelinkApiException(HttpStatusCode.Unauthorized, null, null);

        // Assert
        Assert.False(exception.IsTokenExpired);
    }

    [Theory]
    [InlineData("9035")]
    [InlineData("9036")]
    [InlineData("9037")]
    public void WhenCodeIsAFingerprintRejection_ThenIsFingerprintRejected(string code)
    {
        // Act
        var exception = new BluelinkApiException(HttpStatusCode.BadRequest, code, "server text");

        // Assert
        Assert.True(exception.IsFingerprintRejected);
        Assert.DoesNotContain("server text", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenCodeIs5091_ThenIsRateLimitedAndTheMessageHasNoServerText()
    {
        // Act
        var exception = new BluelinkApiException(HttpStatusCode.TooManyRequests, "5091", "raw server text");

        // Assert
        Assert.True(exception.IsRateLimited);
        Assert.DoesNotContain("raw server text", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenUnknownMessageIsLong_ThenItIsTruncatedTo200Characters()
    {
        // Arrange
        var message = new string('x', 500);

        // Act
        var exception = new BluelinkApiException(HttpStatusCode.BadRequest, "1234", message);

        // Assert
        Assert.Equal("Bluelink returned 400 1234: " + new string('x', 200), exception.Message);
    }

    [Fact]
    public void WhenCommandTimedOut_ThenMessageSaysTheResultDidNotArriveInTime()
    {
        // Act
        var exception = new BluelinkCommandException(BluelinkCommandResult.TimedOut);

        // Assert
        Assert.Equal(BluelinkCommandResult.TimedOut, exception.Result);
        Assert.Equal("The result of the command did not arrive in time.", exception.Message);
    }

    [Theory]
    [InlineData(BluelinkCommandResult.Pending)]
    [InlineData(BluelinkCommandResult.Success)]
    public void WhenResultIsNotAFailure_ThenCommandExceptionThrowsArgumentOutOfRange(BluelinkCommandResult result)
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new BluelinkCommandException(result));
    }
}
