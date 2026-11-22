using Microsoft.Extensions.Time.Testing;
using Namotion.Devices.Hyundai.Client;

namespace Namotion.Devices.Hyundai.Tests;

public class BluelinkFingerprintTests
{
    [Fact]
    public void WhenBodyIsEmpty_ThenFingerprintMatchesThePythonReference()
    {
        // Act
        var fingerprint = BluelinkFingerprint.Compute("1700000000000", string.Empty);

        // Assert
        Assert.Equal("808c3c27e848f33619e117f06ec40bce65714e3f1c79ee22c62d798c1123d600", fingerprint);
    }

    [Fact]
    public void WhenBodyHasWhitespace_ThenFingerprintIgnoresIt()
    {
        // Act
        var fingerprint = BluelinkFingerprint.Compute("1700000000000", "{\"accessToken\": \"a\", \"refreshToken\": \"b\"}");
        var compact = BluelinkFingerprint.Compute("1700000000000", "{\"accessToken\":\"a\",\"refreshToken\":\"b\"}");

        // Assert
        Assert.Equal("ec42b6f11de9ebc4b020f2ff916f0bb292a610cbfebc57e79fd4420df1acaaf1", fingerprint);
        Assert.Equal(fingerprint, compact);
    }

    [Fact]
    public void WhenHeadersAreAdded_ThenTimestampAndFingerprintMatch()
    {
        // Arrange
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/auth/v1/token");

        // Act
        BluelinkFingerprint.AddHeaders(request, "device-1", string.Empty, time);

        // Assert
        Assert.Equal("1700000000000", request.Headers.GetValues("X-Timestamp").Single());
        Assert.Equal("808c3c27e848f33619e117f06ec40bce65714e3f1c79ee22c62d798c1123d600", request.Headers.GetValues("X-Fingerprint").Single());
        Assert.Equal("com.hyundai.oneapp.eu", request.Headers.GetValues("client-id").Single());
        Assert.Equal("device-1", request.Headers.GetValues("client-device-id").Single());
        Assert.EndsWith("_device1_AOS", request.Headers.GetValues("App-Request-Id").Single(), StringComparison.Ordinal);
    }
}
