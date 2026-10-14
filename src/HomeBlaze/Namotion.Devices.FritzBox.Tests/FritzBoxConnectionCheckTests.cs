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
        Assert.Equal("127.0.0.1", result.HostAddress);
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

    [Theory]
    [InlineData("")]
    [InlineData("http://fritz.box")]
    [InlineData("192.168.178.1:49000")]
    [InlineData("fritz box")]
    [InlineData("[fritz.box]")]
    public async Task WhenTheHostAddressIsInvalid_ThenTheCheckSaysSo(string hostAddress)
    {
        // Act
        var result = await FritzBoxConnectionCheck.CheckAsync(hostAddress, "user", "password", CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Null(result.HostAddress);
        Assert.Equal("Enter the router's IP address or host name without http:// or a port.", result.ErrorMessage);
    }

    [Fact]
    public async Task WhenTheTokenIsAlreadyCancelled_ThenCheckAsyncThrows()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FritzBoxConnectionCheck.CheckAsync(Options(fake, fake.Password), cancellationTokenSource.Token));
    }

    [Theory]
    [InlineData("192.168.178.1", "192.168.178.1")]
    [InlineData("  192.168.178.1  ", "192.168.178.1")]
    [InlineData("fritz.box", "fritz.box")]
    [InlineData("fritzbox", "fritzbox")]
    [InlineData("bücher.example", "bücher.example")]
    [InlineData("fd00::1", "fd00::1")]
    [InlineData("[fd00::1]", "fd00::1")]
    public void WhenTheHostAddressIsValid_ThenItIsNormalized(string input, string expected)
    {
        // Act
        var isValid = FritzBoxConnectionCheck.TryNormalizeHostAddress(input, out var normalized);

        // Assert
        Assert.True(isValid);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://fritz.box")]
    [InlineData("https://192.168.178.1")]
    [InlineData("192.168.178.1:49000")]
    [InlineData("[fd00::1]:49000")]
    [InlineData("fritz.box/path")]
    [InlineData("fritz box")]
    [InlineData("fritz\tbox")]
    [InlineData("[fritz.box]")]
    [InlineData("[fd00::1")]
    public void WhenTheHostAddressIsInvalid_ThenItIsRejected(string? input)
    {
        // Act
        var isValid = FritzBoxConnectionCheck.TryNormalizeHostAddress(input, out var normalized);

        // Assert
        Assert.False(isValid);
        Assert.Equal(string.Empty, normalized);
    }

    private static FritzBoxConnectionOptions Options(FakeFritzBox fake, string password) =>
        new("127.0.0.1", fake.Username, password, null) { DescriptionPort = fake.HttpPort };
}
