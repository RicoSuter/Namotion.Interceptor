using System.Collections.Concurrent;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task WhenAReloadedPlaintextSecretHasAValueThatCannotBeRead_ThenAWarningTellsThatTheSecretStaysInTheFile()
    {
        // Arrange
        SecretsPath = Path.Combine(CreateTemporaryDirectory().FullName, "Secrets.json");
        WriteFile("Gateway.json", $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.1" }""");
        var messages = new ConcurrentQueue<string>();
        var storage = CreateStorage(logger: new RecordingLogger(messages));
        await storage.ConnectAsync(CancellationToken.None);
        WriteFile("Gateway.json", $$"""{ "$type": "{{DeviceType}}", "host": [1], "password": "s3cr3t" }""");

        // Act
        await storage.ReconcileAsync(Named("Gateway.json"));

        // Assert
        Assert.Contains(messages, message => message.Contains("Gateway.json") && message.Contains("plaintext"));
        Assert.DoesNotContain(messages, message => message.Contains("s3cr3t"));
    }

    [Fact]
    public async Task WhenTheFileStillHasTheExpectedContent_ThenTheConditionalSaveWritesIt()
    {
        // Arrange
        WriteFile("Gateway.json", $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.1" }""");
        var storage = await ConnectAsync();
        var device = Assert.IsType<EmbeddedSecretDevice>(storage.Children["Gateway"]);
        device.Host = "10.0.0.2";
        var expectedHash = StorageHash.Compute(File.ReadAllBytes(GetFullPath("Gateway.json")));

        // Act
        var isWritten = await storage.WriteConfigurationAsync(device, expectedHash, CancellationToken.None);

        // Assert
        Assert.True(isWritten);
        Assert.Contains("10.0.0.2", File.ReadAllText(GetFullPath("Gateway.json")));
    }

    [Fact]
    public async Task WhenTheFileNoLongerHasTheExpectedContent_ThenTheConditionalSaveWritesNothing()
    {
        // Arrange
        var content = $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.1" }""";
        WriteFile("Gateway.json", content);
        var storage = await ConnectAsync();
        var device = Assert.IsType<EmbeddedSecretDevice>(storage.Children["Gateway"]);
        device.Host = "10.0.0.2";
        var staleHash = StorageHash.Compute("an earlier content"u8);

        // Act
        var isWritten = await storage.WriteConfigurationAsync(device, staleHash, CancellationToken.None);

        // Assert
        Assert.False(isWritten);
        Assert.Equal(content, File.ReadAllText(GetFullPath("Gateway.json")));
    }

    [Fact]
    public async Task WhenABlockArgumentOfAPageHoldsAPlaintextSecret_ThenOnlyItIsReplacedByAReference()
    {
        // Arrange
        SecretsPath = Path.Combine(CreateTemporaryDirectory().FullName, "Secrets.json");
        WriteFile("Gateway.md", $"# Gateway\n\nIntro text.\n\n:: {DeviceType}#gateway Host=10.0.0.1 Password=s3cr3t\n::\n\nOutro text.\n");

        // Act
        var storage = await ConnectAsync();

        // Assert
        var content = await WaitForSecretReferenceOnlyAsync("Gateway.md");
        Assert.StartsWith($"# Gateway\n\nIntro text.\n\n:: {DeviceType}#gateway Host=10.0.0.1 Password=", content);
        Assert.EndsWith("::\n\nOutro text.\n", content);
        Assert.Contains("s3cr3t", File.ReadAllText(SecretsPath));
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);
        await AsyncTestHelpers.WaitUntilAsync(
            () => page.Children.TryGetValue("gateway", out var block) && ((EmbeddedSecretDevice)block).Password == "s3cr3t");
    }

    [Fact]
    public async Task WhenAJsonBlockOfAPageHoldsAPlaintextSecret_ThenItIsReplacedByAReference()
    {
        // Arrange
        SecretsPath = Path.Combine(CreateTemporaryDirectory().FullName, "Secrets.json");
        WriteFile("Gateway.md", $"# Gateway\n\n:: {DeviceType}#gateway\n```json\n{{\n  \"Host\": \"10.0.0.1\",\n  \"Password\": \"s3cr3t\"\n}}\n```\n::\n");

        // Act
        await ConnectAsync();

        // Assert
        var content = await WaitForSecretReferenceOnlyAsync("Gateway.md");
        Assert.StartsWith($"# Gateway\n\n:: {DeviceType}#gateway", content);
        Assert.Contains("10.0.0.1", content);
        Assert.Contains("s3cr3t", File.ReadAllText(SecretsPath));
    }

    [Fact]
    public async Task WhenThePageFileChangedSinceItWasRead_ThenTheBlockRewriteWritesNothingAndReadsThePageAgain()
    {
        // Arrange
        WriteFile("Gateway.md", $"# Gateway\n\n:: {DeviceType}#gateway Host=10.0.0.1\n::\n");
        var storage = await ConnectAsync();
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);
        var device = Assert.IsType<EmbeddedSecretDevice>(page.Children["gateway"]);
        device.Host = "10.0.0.2";
        var handEdit = $"# Gateway\n\nA hand edit.\n\n:: {DeviceType}#gateway Host=10.0.0.1\n::\n";
        WriteFile("Gateway.md", handEdit);

        // Act
        await page.RewriteBlocksIfFileUnchangedAsync([device]);

        // Assert
        Assert.Equal(handEdit, File.ReadAllText(GetFullPath("Gateway.md")));
        Assert.Equal(handEdit, page.Content);
    }

    [Fact]
    public async Task WhenThePageFileIsAsItWasRead_ThenTheBlockRewriteWritesTheBlock()
    {
        // Arrange
        WriteFile("Gateway.md", $"# Gateway\n\n:: {DeviceType}#gateway Host=10.0.0.1\n::\n");
        var storage = await ConnectAsync();
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);
        var device = Assert.IsType<EmbeddedSecretDevice>(page.Children["gateway"]);
        device.Host = "10.0.0.2";

        // Act
        await page.RewriteBlocksIfFileUnchangedAsync([device]);

        // Assert
        Assert.Equal($"# Gateway\n\n:: {DeviceType}#gateway Host=10.0.0.2\n::\n", File.ReadAllText(GetFullPath("Gateway.md")));
    }

    private async Task<string> WaitForSecretReferenceOnlyAsync(string relativePath)
    {
        string? content = null;
        await AsyncTestHelpers.WaitUntilAsync(() =>
        {
            content = TryReadText(relativePath);
            return content is not null && content.Contains("$secret:") && !content.Contains("s3cr3t");
        });

        return content!;
    }

    // A file that is being rewritten can be read empty, or not at all on Windows.
    private string? TryReadText(string relativePath)
    {
        try
        {
            return File.ReadAllText(GetFullPath(relativePath));
        }
        catch (IOException)
        {
            return null;
        }
    }

    // One read for both checks: a file that is being rewritten can be read empty, or not at all on Windows.
    private bool HoldsSecretReferenceOnly(string relativePath)
    {
        try
        {
            var text = File.ReadAllText(GetFullPath(relativePath));
            return text.Contains("\"password\": \"$secret:") && !text.Contains("s3cr3t");
        }
        catch (IOException)
        {
            return false;
        }
    }

    private sealed class RecordingLogger(ConcurrentQueue<string> messages) : ILogger<FluentStorageContainer>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => messages.Enqueue(formatter(state, exception));
    }
}
