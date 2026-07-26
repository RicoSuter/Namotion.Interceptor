using System.Reflection;
using HueApi.BridgeLocator;
using Xunit;

namespace Namotion.Devices.Philips.Hue.Tests;

public class HueBridgeClientLifecycleTests
{
    [Fact]
    public async Task WhenUsedClientIsReset_ThenRetryUsesFreshHttpClient()
    {
        // Arrange
        var bridge = TestHelpers.CreateTestBridge();
        bridge.AppKey = "test-key";
        SetPrivateField(bridge, "_bridge", new LocatedBridge("test-bridge-id", "127.0.0.1", null));

        var firstClient = bridge.GetOrCreateClient();
        var firstHttpClient = GetPrivateField<HttpClient>(bridge, "_httpClient");
        using var request = new HttpRequestMessage(HttpMethod.Get, "clip/v2/resource/device");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => firstHttpClient.SendAsync(request, new CancellationToken(canceled: true)));

        // Act
        bridge.ResetClient(firstClient);
        var secondClient = bridge.GetOrCreateClient();
        var secondHttpClient = GetPrivateField<HttpClient>(bridge, "_httpClient");

        // Assert
        Assert.NotSame(firstClient, secondClient);
        Assert.NotSame(firstHttpClient, secondHttpClient);

        bridge.ResetClient(secondClient);
    }

    private static T GetPrivateField<T>(HueBridge bridge, string fieldName) where T : class
    {
        return (T)(typeof(HueBridge)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(bridge)
            ?? throw new InvalidOperationException($"Field '{fieldName}' has no value."));
    }

    private static void SetPrivateField<T>(HueBridge bridge, string fieldName, T value)
    {
        var field = typeof(HueBridge).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' was not found.");
        field.SetValue(bridge, value);
    }
}
