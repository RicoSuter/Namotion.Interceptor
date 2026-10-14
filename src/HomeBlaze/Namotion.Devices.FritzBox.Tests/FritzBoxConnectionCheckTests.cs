using Namotion.Devices.FritzBox.Client;
using Namotion.Devices.FritzBox.Tests.Testing;
using Namotion.Interceptor.Testing;
using Xunit;

namespace Namotion.Devices.FritzBox.Tests;

public class FritzBoxConnectionCheckTests
{
    [Fact]
    public async Task WhenTheLoginSucceeds_ThenTheModelAndTheCertificateAreReported()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var expected = CertificateInfo.Create(fake.Certificate);

        // Act
        var result = await FritzBoxConnectionCheck.CheckAsync(Options(fake, fake.Password), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Null(result.ErrorMessage);
        Assert.Equal("FRITZ!Box 7590", result.Model);
        Assert.Equal(expected.Pin, result.CertificatePin);
        Assert.Equal(expected.Fingerprint, result.CertificateFingerprint);
    }

    [Fact]
    public async Task WhenTheLoginFails_ThenTheCheckSaysSo()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();

        // Act
        var result = await FritzBoxConnectionCheck.CheckAsync(Options(fake, "wrong"), CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Contains("Login failed", result.ErrorMessage);
    }

    [Fact]
    public async Task WhenTheRouterIsNotReachable_ThenTheCheckSaysSo()
    {
        // Arrange
        var options = new FritzBoxConnectionOptions("127.0.0.1", "user", "password", null) { DescriptionPort = LoopbackPorts.GetFreePort() };

        // Act
        var result = await FritzBoxConnectionCheck.CheckAsync(options, CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
    }

    private static FritzBoxConnectionOptions Options(FakeFritzBox fake, string password) =>
        new("127.0.0.1", fake.Username, password, null) { DescriptionPort = fake.HttpPort };
}
