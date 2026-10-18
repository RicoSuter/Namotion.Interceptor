using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Namotion.Devices.Nuki.Client;
using Namotion.Devices.Nuki.Tests.Testing;
using Namotion.Interceptor.Testing;
using Xunit;

namespace Namotion.Devices.Nuki.Tests;

public class NukiBridgeClientTests
{
    private const string Token = "123456";

    private static NukiBridgeClient CreateClient(FakeNukiBridge bridge, NukiTokenMode tokenMode, string hostAddress = "192.168.1.50")
        => new(bridge.CreateClient(string.Empty), hostAddress, Token, tokenMode, busyRetryDelay: TimeSpan.Zero);

    [Theory]
    [InlineData("192.168.1.50", "http://192.168.1.50:8080/info?token=123456")]
    [InlineData("192.168.1.50:8081", "http://192.168.1.50:8081/info?token=123456")]
    [InlineData("http://192.168.1.50/", "http://192.168.1.50:8080/info?token=123456")]
    [InlineData("https://192.168.1.50", "http://192.168.1.50:8080/info?token=123456")]
    [InlineData("HTTPS://192.168.1.50:8081/", "http://192.168.1.50:8081/info?token=123456")]
    [InlineData("[fe80::1]", "http://[fe80::1]:8080/info?token=123456")]
    [InlineData("[fe80::1]:8081", "http://[fe80::1]:8081/info?token=123456")]
    [InlineData("fe80::1", "http://[fe80::1]:8080/info?token=123456")]
    public async Task WhenTokenModeIsPlain_ThenTheRequestGoesToTheNormalizedHostAddressWithTheTokenAsQueryParameter(string hostAddress, string expectedUri)
    {
        // Arrange
        var bridge = new FakeNukiBridge();
        var client = CreateClient(bridge, NukiTokenMode.Plain, hostAddress);

        // Act
        await client.GetInfoAsync(CancellationToken.None);

        // Assert
        Assert.Equal(expectedUri, Assert.Single(bridge.Requests).ToString());
    }

    [Fact]
    public async Task WhenTokenModeIsEncrypted_ThenTheRequestCarriesACtokenThatDecryptsToTimestampAndRandomNumber()
    {
        // Arrange
        var bridge = new FakeNukiBridge();
        var client = CreateClient(bridge, NukiTokenMode.Encrypted);

        // Act
        await client.GetInfoAsync(CancellationToken.None);

        // Assert
        var query = HttpUtility.ParseQueryString(Assert.Single(bridge.Requests).Query);
        Assert.Null(query["token"]);
        var ctoken = Convert.FromHexString(query["ctoken"]!);
        var nonce = Convert.FromHexString(query["nonce"]!);
        Assert.Equal(24, nonce.Length);

        var plaintext = Encoding.ASCII.GetString(NaClReference.Open(ctoken, nonce, SHA256.HashData(Encoding.UTF8.GetBytes(Token))));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z,\d{1,5}$", plaintext);
        var timestamp = DateTimeOffset.Parse(plaintext.Split(',')[0], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(DateTimeOffset.UtcNow - timestamp, TimeSpan.FromSeconds(-1), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task WhenBridgeIsBusyOnce_ThenTheRequestIsRetried()
    {
        // Arrange
        var bridge = new FakeNukiBridge();
        var calls = 0;
        bridge.StatusOverride = path => Interlocked.Increment(ref calls) == 1 ? HttpStatusCode.ServiceUnavailable : null;
        var client = CreateClient(bridge, NukiTokenMode.Plain);

        // Act
        var devices = await client.GetListAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, devices.Length);
        Assert.Equal(2, bridge.CountRequests("list"));
    }

    [Fact]
    public async Task WhenBridgeIsBusyOnceInEncryptedMode_ThenTheRetryUsesADifferentNonce()
    {
        // Arrange
        var bridge = new FakeNukiBridge();
        var calls = 0;
        bridge.StatusOverride = path => Interlocked.Increment(ref calls) == 1 ? HttpStatusCode.ServiceUnavailable : null;
        var client = CreateClient(bridge, NukiTokenMode.Encrypted);

        // Act
        await client.GetInfoAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, bridge.CountRequests("info"));
        var nonces = bridge.Requests.Select(uri => HttpUtility.ParseQueryString(uri.Query)["nonce"]).ToArray();
        Assert.NotEqual(nonces[0], nonces[1]);
    }

    [Fact]
    public async Task WhenBridgeStaysBusy_ThenTheSecondAttemptThrows()
    {
        // Arrange
        var bridge = new FakeNukiBridge { StatusOverride = _ => HttpStatusCode.ServiceUnavailable };
        var client = CreateClient(bridge, NukiTokenMode.Plain);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetListAsync(CancellationToken.None));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Equal(2, bridge.CountRequests("list"));
    }

    [Fact]
    public async Task WhenTokenIsRejected_ThenTheMessageNamesTheLikelyCauses()
    {
        // Arrange
        var bridge = new FakeNukiBridge { StatusOverride = _ => HttpStatusCode.Unauthorized };
        var client = CreateClient(bridge, NukiTokenMode.Encrypted);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetInfoAsync(CancellationToken.None));
        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Contains("Token rejected", exception.Message);
        Assert.Contains("clock", exception.Message);
        Assert.Contains("Plain", exception.Message);
    }

    [Fact]
    public async Task WhenLockActionIsSent_ThenItWaitsForTheResult()
    {
        // Arrange
        var bridge = new FakeNukiBridge();
        var client = CreateClient(bridge, NukiTokenMode.Plain);

        // Act
        var result = await client.LockActionAsync(nukiId: 7, deviceType: 4, action: 2, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        var query = HttpUtility.ParseQueryString(Assert.Single(bridge.Requests).Query);
        Assert.Equal("7", query["nukiId"]);
        Assert.Equal("4", query["deviceType"]);
        Assert.Equal("2", query["action"]);
        Assert.Equal("0", query["nowait"]);
    }

    [Fact]
    public async Task WhenCallerCancelsDuringLockAction_ThenOperationCanceledExceptionIsThrownInsteadOfTimeoutException()
    {
        // Arrange
        var bridge = new FakeNukiBridge { ActionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var client = CreateClient(bridge, NukiTokenMode.Plain);
        using var cancellationSource = new CancellationTokenSource();

        // Act
        var lockActionTask = client.LockActionAsync(nukiId: 1, deviceType: 0, action: 1, cancellationSource.Token);
        await cancellationSource.CancelAsync();

        // Assert
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lockActionTask);
        Assert.IsNotType<TimeoutException>(exception);
    }

    [Fact]
    public async Task WhenTheHttpClientIsDisposedDuringLockAction_ThenOperationCanceledExceptionIsThrownInsteadOfTimeoutException()
    {
        // Arrange
        var bridge = new FakeNukiBridge { ActionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var httpClient = bridge.CreateClient(string.Empty);
        var client = new NukiBridgeClient(httpClient, "192.168.1.50", Token, NukiTokenMode.Plain);
        var lockActionTask = client.LockActionAsync(nukiId: 1, deviceType: 0, action: 1, CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => bridge.CountRequests("lockAction") == 1, message: "The action was not sent.");

        // Act
        httpClient.Dispose();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lockActionTask);
    }

    [Fact]
    public async Task WhenListIsRequested_ThenTheResponseMapsTheRenamedFields()
    {
        // Arrange
        var bridge = new FakeNukiBridge();
        var client = CreateClient(bridge, NukiTokenMode.Plain);

        // Act
        var devices = await client.GetListAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, devices.Single(device => device.NukiId == 1).LastKnownState!.DoorSensorState);
        Assert.NotNull(devices.Single(device => device.NukiId == 2).LastKnownState!.RingActionTimestamp);
    }

    [Fact]
    public async Task WhenAuthIsSent_ThenNoTokenIsAdded()
    {
        // Arrange
        var bridge = new FakeNukiBridge();
        var client = new NukiBridgeClient(bridge.CreateClient(string.Empty), "192.168.1.50", token: null, NukiTokenMode.Plain);

        // Act
        var result = await client.AuthAsync(CancellationToken.None);

        // Assert
        Assert.Equal("abc123", result.Token);
        Assert.Equal("http://192.168.1.50:8080/auth", Assert.Single(bridge.Requests).ToString());
    }
}
