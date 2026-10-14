using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.FritzBox.Client;
using Namotion.Devices.FritzBox.Tests.Testing;
using Xunit;

namespace Namotion.Devices.FritzBox.Tests;

public class FritzBoxConnectionTests
{
    [Fact]
    public async Task WhenOpeningWithoutAPin_ThenTheLoginSucceedsAndThePresentedKeyIsReported()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();

        // Act
        using var connection = await FritzBoxConnection.OpenAsync(Options(fake), NullLogger.Instance, CancellationToken.None);
        var info = await connection.CheckLoginAsync(CancellationToken.None);

        // Assert
        Assert.Equal("FRITZ!Box 7590", info.GetString("NewModelName"));
        Assert.Equal(CertificateInfo.Create(fake.Certificate), connection.PresentedCertificate);
        Assert.Equal(new Uri($"https://127.0.0.1:{fake.HttpsPort}/"), connection.BaseUri);
    }

    [Fact]
    public async Task WhenThePinMatches_ThenRequestsSucceed()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var pin = CertificateInfo.Create(fake.Certificate).Pin;
        using var connection = await FritzBoxConnection.OpenAsync(Options(fake, pin: pin), NullLogger.Instance, CancellationToken.None);

        // Act
        var exception = await Record.ExceptionAsync(() => connection.CheckLoginAsync(CancellationToken.None));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public async Task WhenThePinDiffers_ThenTheLoginCheckFailsWithThePresentedKey()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        using var other = FakeFritzBox.CreateCertificate();
        var otherPin = CertificateInfo.Create(other).Pin;
        using var connection = await FritzBoxConnection.OpenAsync(Options(fake, pin: otherPin), NullLogger.Instance, CancellationToken.None);

        // Act
        var exception = await Assert.ThrowsAsync<FritzBoxCertificateException>(() => connection.CheckLoginAsync(CancellationToken.None));

        // Assert
        Assert.Equal(CertificateInfo.Create(fake.Certificate), exception.Presented);
    }

    [Fact]
    public async Task WhenThePasswordIsWrong_ThenTheLoginCheckFailsAfterOneFailedAttempt()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        using var connection = await FritzBoxConnection.OpenAsync(Options(fake, password: "wrong"), NullLogger.Instance, CancellationToken.None);

        // Act
        var exception = await Assert.ThrowsAsync<FritzBoxLoginException>(() => connection.CheckLoginAsync(CancellationToken.None));

        // Assert
        Assert.Contains("Login failed", exception.Message);
        Assert.Equal(1, fake.FailedLogins);
    }

    [Fact]
    public async Task WhenAnOptionalActionIsRefused_ThenReadingItReturnsNull()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        fake.Faults["X_AVM-DE_GetOnlineMonitor"] = 606;
        using var connection = await FritzBoxConnection.OpenAsync(Options(fake), NullLogger.Instance, CancellationToken.None);

        // Act
        var answer = await connection.ReadOrDefaultAsync(Tr64Services.WanCommonInterfaceConfig, "X_AVM-DE_GetOnlineMonitor", [("NewSyncGroupIndex", "0")], CancellationToken.None);

        // Assert
        Assert.Null(answer);
    }

    [Fact]
    public async Task WhenAServiceIsNotOffered_ThenReadingItReturnsNull()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        using var connection = await FritzBoxConnection.OpenAsync(Options(fake), NullLogger.Instance, CancellationToken.None);

        // Act
        var answer = await connection.ReadOrDefaultAsync("urn:dslforum-org:service:X_AVM-DE_Dect:1", "GetNumberOfDectEntries", null, CancellationToken.None);

        // Assert
        Assert.Null(answer);
    }

    [Fact]
    public async Task WhenTheHostListIsDownloaded_ThenItsBytesAreReturned()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        using var connection = await FritzBoxConnection.OpenAsync(Options(fake), NullLogger.Instance, CancellationToken.None);

        // Act
        await using var content = await connection.DownloadAsync(FakeFritzBox.HostListPath, FritzBoxConnection.MaximumListBytes, CancellationToken.None);

        // Assert
        using var reader = new StreamReader(content);
        Assert.Equal(fake.HostListXml, await reader.ReadToEndAsync());
    }

    [Theory]
    [InlineData("//attacker/list")]
    [InlineData("http://attacker/list")]
    public async Task WhenAListPathIsNotOnTheRouter_ThenTheDownloadIsRefused(string path)
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        using var connection = await FritzBoxConnection.OpenAsync(Options(fake), NullLogger.Instance, CancellationToken.None);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => connection.DownloadAsync(path, FritzBoxConnection.MaximumListBytes, CancellationToken.None));
    }

    [Fact]
    public async Task WhenAListIsLargerThanAllowed_ThenTheDownloadIsRefused()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        using var connection = await FritzBoxConnection.OpenAsync(Options(fake), NullLogger.Instance, CancellationToken.None);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => connection.DownloadAsync(FakeFritzBox.HostListPath, 100, CancellationToken.None));
    }

    [Fact]
    public async Task WhenTheDefaultConnectionServiceIsReported_ThenItIsUsed()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        using var connection = await FritzBoxConnection.OpenAsync(Options(fake), NullLogger.Instance, CancellationToken.None);

        // Act
        await connection.ResolveInternetServiceAsync(CancellationToken.None);

        // Assert
        Assert.Equal(Tr64Services.WanPppConnection, connection.InternetServiceType);
    }

    [Fact]
    public async Task WhenTheDefaultConnectionServiceIsUnavailable_ThenTheConnectedServiceIsUsed()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        fake.Faults["GetDefaultConnectionService"] = 401;
        fake.Answers[$"{Tr64Services.WanPppConnection}#GetStatusInfo"] = "<NewConnectionStatus>Unconfigured</NewConnectionStatus>";
        fake.Answers[$"{Tr64Services.WanIpConnection}#GetStatusInfo"] = "<NewConnectionStatus>Connected</NewConnectionStatus>";
        using var connection = await FritzBoxConnection.OpenAsync(Options(fake), NullLogger.Instance, CancellationToken.None);

        // Act
        await connection.ResolveInternetServiceAsync(CancellationToken.None);

        // Assert
        Assert.Equal(Tr64Services.WanIpConnection, connection.InternetServiceType);
    }

    private static FritzBoxConnectionOptions Options(FakeFritzBox fake, string? password = null, string? pin = null) =>
        new("127.0.0.1", fake.Username, password ?? fake.Password, pin) { DescriptionPort = fake.HttpPort };
}
