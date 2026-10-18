using System.Net;
using Namotion.Devices.Nuki.Client;
using Namotion.Devices.Nuki.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Nuki.Tests;

/// <summary>
/// Reads a real bridge. Read-only: sends no lock action and never writes the token to the output.
/// </summary>
[Trait("Category", "Integration")]
public class NukiBridgeIntegrationTests
{
    [RealNukiBridgeTheory]
    [InlineData(NukiTokenMode.Plain)]
    [InlineData(NukiTokenMode.Encrypted)]
    public async Task WhenARealBridgeIsRead_ThenItAnswersInfoAndListInTheTokenMode(NukiTokenMode tokenMode)
    {
        // Arrange
        var hostAddress = Environment.GetEnvironmentVariable(RealNukiBridgeTheoryAttribute.HostVariable)!;
        var token = Environment.GetEnvironmentVariable(RealNukiBridgeTheoryAttribute.TokenVariable)!;
        using var httpClient = new HttpClient();
        var plainInfo = await new NukiBridgeClient(httpClient, hostAddress, token, NukiTokenMode.Plain).GetInfoAsync(CancellationToken.None);
        var client = new NukiBridgeClient(httpClient, hostAddress, token, tokenMode);

        if (tokenMode == NukiTokenMode.Encrypted && NukiStateMapping.ToBridgeType(plainInfo.BridgeType) == NukiBridgeType.Software)
        {
            // Act & Assert: a software bridge accepts plain tokens only.
            var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetInfoAsync(CancellationToken.None));
            Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
            return;
        }

        // Act
        var info = await client.GetInfoAsync(CancellationToken.None);
        var devices = await client.GetListAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(NukiStateMapping.ToBridgeType(info.BridgeType));
        Assert.All(devices, device => Assert.True(device.NukiId > 0));
    }
}
