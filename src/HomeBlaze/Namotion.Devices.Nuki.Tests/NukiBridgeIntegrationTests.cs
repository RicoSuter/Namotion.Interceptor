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
        var client = new NukiBridgeClient(httpClient, hostAddress, token, tokenMode);

        async Task AssertInfoAndListAsync(NukiBridgeClient bridgeClient)
        {
            var info = await bridgeClient.GetInfoAsync(CancellationToken.None);
            var devices = await bridgeClient.GetListAsync(CancellationToken.None);

            Assert.NotNull(NukiStateMapping.ToBridgeType(info.BridgeType));
            Assert.All(devices, device => Assert.True(device.NukiId > 0));
        }

        if (tokenMode != NukiTokenMode.Encrypted)
        {
            // Act & Assert
            await AssertInfoAndListAsync(client);
            return;
        }

        try
        {
            // Act & Assert
            await AssertInfoAndListAsync(client);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Act & Assert: as DetectTokenModeAsync does, read the bridge type in Plain only after the encrypted
            // request is rejected, so the token is never sent in plain text against a hardware bridge.
            var plainInfo = await new NukiBridgeClient(httpClient, hostAddress, token, NukiTokenMode.Plain).GetInfoAsync(CancellationToken.None);
            Assert.Equal(NukiBridgeType.Software, NukiStateMapping.ToBridgeType(plainInfo.BridgeType));
        }
    }
}
