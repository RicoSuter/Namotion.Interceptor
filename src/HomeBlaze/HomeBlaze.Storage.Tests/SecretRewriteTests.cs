using Namotion.Interceptor.Testing;

namespace HomeBlaze.Storage.Tests;

public class SecretRewriteTests : StorageTestBase
{
    private static readonly string DeviceType = typeof(EmbeddedSecretDevice).FullName!;

    [Fact]
    public async Task WhenAJsonFileHoldsAPlaintextSecret_ThenItIsMovedToTheSecretStoreOnLoad()
    {
        // Arrange
        SecretsPath = Path.Combine(CreateTemporaryDirectory().FullName, "Secrets.json");
        WriteFile("Gateway.json", $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.1", "password": "s3cr3t" }""");

        // Act
        var storage = await ConnectAsync();

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => HoldsSecretReferenceOnly("Gateway.json"));
        Assert.Contains("s3cr3t", File.ReadAllText(SecretsPath));
        Assert.Equal("s3cr3t", Assert.IsType<EmbeddedSecretDevice>(storage.Children["Gateway"]).Password);
    }

    [Fact]
    public async Task WhenAPlaintextSecretIsWrittenIntoAWatchedJsonFile_ThenItIsMovedToTheSecretStoreOnReload()
    {
        // Arrange
        SecretsPath = Path.Combine(CreateTemporaryDirectory().FullName, "Secrets.json");
        WriteFile("Gateway.json", $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.1" }""");
        var storage = await ConnectAsync(enableFileWatching: true);
        var device = Assert.IsType<EmbeddedSecretDevice>(storage.Children["Gateway"]);

        // Act
        WriteFile("Gateway.json", $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.1", "password": "s3cr3t" }""");

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => device.Password == "s3cr3t" && HoldsSecretReferenceOnly("Gateway.json"), WatcherTimeout);
        Assert.Contains("s3cr3t", File.ReadAllText(SecretsPath));
    }

    // One read for both checks: a file that is being rewritten can be read empty.
    private bool HoldsSecretReferenceOnly(string relativePath)
    {
        var text = File.ReadAllText(GetFullPath(relativePath));
        return text.Contains("\"password\": \"$secret:") && !text.Contains("s3cr3t");
    }
}
