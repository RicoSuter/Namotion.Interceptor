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
    [RealNukiBridgeFact]
    public async Task WhenARealBridgeIsPaired_ThenItAnswersInfoAndListInTheDetectedTokenMode()
    {
        // Arrange
        var hostAddress = Environment.GetEnvironmentVariable(RealNukiBridgeFactAttribute.HostVariable)!;
        var token = Environment.GetEnvironmentVariable(RealNukiBridgeFactAttribute.TokenVariable)!;
        using var httpClient = new HttpClient();

        // Act
        var detection = await NukiBridgeDiscovery.DetectTokenModeAsync(httpClient, hostAddress, token, CancellationToken.None);
        var client = new NukiBridgeClient(httpClient, hostAddress, token, detection.Mode);
        var info = await client.GetInfoAsync(CancellationToken.None);
        var devices = await client.GetListAsync(CancellationToken.None);

        // Assert
        Assert.Null(detection.Warning);
        Assert.NotNull(NukiStateMapping.ToBridgeType(info.BridgeType));
        Assert.All(devices, device => Assert.True(device.NukiId > 0));
    }
}
