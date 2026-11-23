using System.Collections.Concurrent;
using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.Storage.Tests;

public class SecretRewriteTests : StorageTestBase
{
    private static readonly string DeviceType = typeof(EmbeddedSecretDevice).FullName!;

    [Fact]
    public async Task WhenAJsonFileHoldsAPlaintextSecret_ThenItIsMovedToTheSecretStoreOnLoad()
    {
        // Arrange
        var secretsPath = UseSecretStore();
        WriteFile("Gateway.json", $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.1", "password": "s3cr3t" }""");

        // Act
        var storage = await ConnectAsync();

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => HoldsSecretReferenceOnly("Gateway.json"));
        Assert.Contains("s3cr3t", File.ReadAllText(secretsPath));
        Assert.Equal("s3cr3t", Assert.IsType<EmbeddedSecretDevice>(storage.Children["Gateway"]).Password);
    }

    [Fact]
    public async Task WhenAUtf16JsonFileHoldsAPlaintextSecret_ThenItIsMovedToTheSecretStoreOnLoad()
    {
        // Arrange
        var secretsPath = UseSecretStore();
        File.WriteAllText(
            GetFullPath("Gateway.json"),
            $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.1", "password": "s3cr3t" }""",
            System.Text.Encoding.Unicode);

        // Act
        await ConnectAsync();

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => HoldsSecretReferenceOnly("Gateway.json"));
        Assert.Contains("s3cr3t", File.ReadAllText(secretsPath));
    }

    [Fact]
    public async Task WhenAPlaintextSecretIsWrittenIntoAWatchedJsonFile_ThenItIsMovedToTheSecretStoreOnReload()
    {
        // Arrange
        var secretsPath = UseSecretStore();
        WriteFile("Gateway.json", $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.1" }""");
        var storage = await ConnectAsync(enableFileWatching: true);
        var device = Assert.IsType<EmbeddedSecretDevice>(storage.Children["Gateway"]);

        // Act
        WriteFile("Gateway.json", $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.1", "password": "s3cr3t" }""");

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => device.Password == "s3cr3t" && HoldsSecretReferenceOnly("Gateway.json"), WatcherTimeout);
        Assert.Contains("s3cr3t", File.ReadAllText(secretsPath));
    }

    [Fact]
    public async Task WhenAReloadedPlaintextSecretHasAValueThatCannotBeRead_ThenAWarningTellsThatTheSecretStaysInTheFile()
    {
        // Arrange
        UseSecretStore();
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
    public async Task WhenCheckingAReloadedJsonFileForPlaintextSecretsFails_ThenTheConfigurationIsAppliedAndAWarningIsLogged()
    {
        // Arrange
        UseSecretStore();
        var deviceType = typeof(UnserializableSecretDevice).FullName;
        WriteFile("Gateway.json", $$"""{ "$type": "{{deviceType}}", "value": {} }""");
        var messages = new ConcurrentQueue<string>();
        var storage = CreateStorage(logger: new RecordingLogger(messages));
        await storage.ConnectAsync(CancellationToken.None);
        var device = Assert.IsType<UnserializableSecretDevice>(storage.Children["Gateway"]);
        var applyCount = device.ApplyCount;
        WriteFile("Gateway.json", $$"""{ "$type": "{{deviceType}}", "value": {}, "password": "s3cr3t" }""");

        // The load logs the same warning, because the check of this type always fails.
        messages.Clear();

        // Act
        await storage.ReconcileAsync(Named("Gateway.json"));

        // Assert
        Assert.Same(device, storage.Children["Gateway"]);
        Assert.Equal("s3cr3t", device.Password);
        Assert.Equal(applyCount + 1, device.ApplyCount);
        Assert.Contains(messages, message => message.Contains("Gateway.json") && message.Contains("plaintext"));
        Assert.DoesNotContain(messages, message => message.Contains("s3cr3t"));
    }

    [Fact]
    public async Task WhenASecretChangesBetweenTwoSaves_ThenTheFileKeepsItsContentAndTheStoreHoldsOnlyTheLatestValue()
    {
        // Arrange
        var secretsPath = UseSecretStore();
        WriteFile("Gateway.json", $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.1" }""");
        var storage = await ConnectAsync();
        var device = Assert.IsType<EmbeddedSecretDevice>(storage.Children["Gateway"]);
        device.Password = "first";
        await storage.WriteConfigurationAsync(device, CancellationToken.None);
        var firstContent = File.ReadAllText(GetFullPath("Gateway.json"));
        device.Password = "second";

        // Act
        await storage.WriteConfigurationAsync(device, CancellationToken.None);

        // Assert
        Assert.Contains("\"password\": \"$secret:", firstContent);
        Assert.Equal(firstContent, File.ReadAllText(GetFullPath("Gateway.json")));
        var entry = Assert.Single(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(secretsPath))!);
        Assert.Equal("second", entry.Value);
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
    public async Task WhenTheFileChangedOnDiskBeforeAPassSawIt_ThenTheConditionalSaveWritesNothing()
    {
        // Arrange: without file watching, no pass sees the hand edit, so the index still holds the hash as read.
        WriteFile("Gateway.json", $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.1" }""");
        var storage = await ConnectAsync();
        var device = Assert.IsType<EmbeddedSecretDevice>(storage.Children["Gateway"]);
        var hashAsRead = StorageHash.Compute(File.ReadAllBytes(GetFullPath("Gateway.json")));
        var handEdit = $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.9" }""";
        WriteFile("Gateway.json", handEdit);

        // Act
        var isWritten = await storage.WriteConfigurationAsync(device, hashAsRead, CancellationToken.None);

        // Assert
        Assert.False(isWritten);
        Assert.Equal(handEdit, File.ReadAllText(GetFullPath("Gateway.json")));
    }

    [Fact]
    public async Task WhenAJsonFileWithAPlaintextSecretHasAKeyTheTypeDoesNotKnow_ThenTheConditionalSaveWritesNothingAndLogsTheKey()
    {
        // Arrange
        UseSecretStore();
        var content = $$"""{ "$type": "{{DeviceType}}", "hostAdress": "10.0.0.1", "password": "s3cr3t" }""";
        WriteFile("Gateway.json", content);
        var messages = new ConcurrentQueue<string>();
        var storage = CreateStorage(logger: new RecordingLogger(messages));
        await storage.ConnectAsync(CancellationToken.None);
        var device = Assert.IsType<EmbeddedSecretDevice>(storage.Children["Gateway"]);
        var hash = StorageHash.Compute(File.ReadAllBytes(GetFullPath("Gateway.json")));
        messages.Clear();

        // Act
        var isWritten = await storage.WriteConfigurationAsync(device, hash, CancellationToken.None);

        // Assert
        Assert.False(isWritten);
        Assert.Equal(content, File.ReadAllText(GetFullPath("Gateway.json")));
        Assert.Contains(messages, message => message.Contains("Gateway.json") && message.Contains("hostAdress"));
        Assert.DoesNotContain(messages, message => message.Contains("s3cr3t") || message.Contains("10.0.0.1"));
    }

    [Fact]
    public async Task WhenABlockArgumentOfAPageHoldsAPlaintextSecret_ThenOnlyItIsReplacedByAReference()
    {
        // Arrange
        var secretsPath = UseSecretStore();
        WriteFile("Gateway.md", $"# Gateway\n\nIntro text.\n\n:: {DeviceType}#gateway Host=10.0.0.1 Password=s3cr3t\n::\n\nOutro text.\n");

        // Act
        var storage = await ConnectAsync();

        // Assert
        var content = await WaitForSecretReferenceOnlyAsync("Gateway.md");
        Assert.StartsWith($"# Gateway\n\nIntro text.\n\n:: {DeviceType}#gateway Host=10.0.0.1 Password=", content);
        Assert.EndsWith("::\n\nOutro text.\n", content);
        Assert.Contains("s3cr3t", File.ReadAllText(secretsPath));
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);
        await AsyncTestHelpers.WaitUntilAsync(
            () => page.Children.TryGetValue("gateway", out var block) && ((EmbeddedSecretDevice)block).Password == "s3cr3t");
    }

    [Fact]
    public async Task WhenAJsonBlockOfAPageHoldsAPlaintextSecret_ThenItIsReplacedByAReference()
    {
        // Arrange
        var secretsPath = UseSecretStore();
        WriteFile("Gateway.md", $"# Gateway\n\n:: {DeviceType}#gateway\n```json\n{{\n  \"Host\": \"10.0.0.1\",\n  \"Password\": \"s3cr3t\"\n}}\n```\n::\n");

        // Act
        await ConnectAsync();

        // Assert
        var content = await WaitForSecretReferenceOnlyAsync("Gateway.md");
        Assert.StartsWith($"# Gateway\n\n:: {DeviceType}#gateway", content);
        Assert.Contains("10.0.0.1", content);
        Assert.Contains("s3cr3t", File.ReadAllText(secretsPath));
    }

    [Fact]
    public async Task WhenABlockWithAPlaintextSecretHasANestedKeyTheTypeDoesNotKnow_ThenItIsNotRewritten()
    {
        // Arrange
        UseSecretStore();
        var deviceType = typeof(NestedSecretDevice).FullName;
        var content = $"# Gateway\n\n:: {deviceType}#gateway Password=s3cr3t\n```json\n{{\n  \"Options\": {{ \"Endpoint\": \"a\", \"Typo\": 1 }}\n}}\n```\n::\n";
        WriteFile("Gateway.md", content);
        var storage = await ConnectAsync();
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);
        var device = Assert.IsType<NestedSecretDevice>(page.Children["gateway"]);

        // Act: the load scheduled the rewrite, and this one runs after it.
        await page.RewriteBlocksIfFileUnchangedAsync([device]);

        // Assert
        Assert.Equal("s3cr3t", device.Password);
        Assert.Equal(content, File.ReadAllText(GetFullPath("Gateway.md")));
    }

    [Fact]
    public async Task WhenThePageFileChangedSinceItWasRead_ThenTheBlockRewriteWritesNothingAndReadsThePageAgain()
    {
        // Arrange
        var (page, movedContent) = await ConnectAndWaitForMovedSecretAsync($"# Gateway\n\n:: {DeviceType}#gateway Host=10.0.0.1 Password=s3cr3t\n::\n");
        var device = Assert.IsType<EmbeddedSecretDevice>(page.Children["gateway"]);
        device.Host = "10.0.0.2";
        var handEdit = movedContent.Replace("# Gateway\n", "# Gateway\n\nA hand edit.\n");
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
        var (page, _) = await ConnectAndWaitForMovedSecretAsync($"# Gateway\n\n:: {DeviceType}#gateway Host=10.0.0.1 Password=s3cr3t\n::\n");
        var device = Assert.IsType<EmbeddedSecretDevice>(page.Children["gateway"]);
        device.Host = "10.0.0.2";

        // Act
        await page.RewriteBlocksIfFileUnchangedAsync([device]);

        // Assert
        Assert.StartsWith($"# Gateway\n\n:: {DeviceType}#gateway Host=10.0.0.2 Password=$secret:", File.ReadAllText(GetFullPath("Gateway.md")));
    }

    [Fact]
    public async Task WhenNoParseMarkedTheSubject_ThenTheBlockRewriteWritesNothing()
    {
        // Arrange: without a secret store, no parse marks a subject.
        var content = $"# Gateway\n\n:: {DeviceType}#gateway Host=10.0.0.1\n::\n";
        WriteFile("Gateway.md", content);
        var storage = await ConnectAsync();
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);
        var device = Assert.IsType<EmbeddedSecretDevice>(page.Children["gateway"]);
        device.Host = "10.0.0.2";

        // Act
        await page.RewriteBlocksIfFileUnchangedAsync([device]);

        // Assert
        Assert.Equal(content, File.ReadAllText(GetFullPath("Gateway.md")));
    }

    [Fact]
    public async Task WhenABlockWithAPlaintextSecretHasAValueThatCannotBeRead_ThenItIsNotRewritten()
    {
        // Arrange: the blocks are rewritten in the order of the page, so the error block would be written first.
        var secretsPath = UseSecretStore();
        var errorBlock = $":: {DeviceType}#broken\n```json\n{{\n  \"Host\": [1],\n  \"Password\": \"error-secret\"\n}}\n```\n::\n";
        WriteFile("Gateway.md", $"# Gateway\n\n{errorBlock}\n:: {DeviceType}#gateway Host=10.0.0.1 Password=s3cr3t\n::\n");

        // Act
        var storage = await ConnectAsync();

        // Assert
        var content = await WaitForSecretReferenceOnlyAsync("Gateway.md");
        Assert.StartsWith($"# Gateway\n\n{errorBlock}\n", content);
        Assert.DoesNotContain("error-secret", File.ReadAllText(secretsPath));
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);
        Assert.False(page.Children.ContainsKey("broken"));
    }

    [Fact]
    public async Task WhenTheRewriteOfABlockIsRefused_ThenItIsTriedAgainOnlyOnceItsConfigurationChanges()
    {
        // Arrange
        UseSecretStore();
        var deviceType = typeof(UnwritableSecretDevice).FullName;
        WriteFile("Gateway.md", $"# Gateway\n\n:: {deviceType}#gateway Password=s3cr3t\n::\n");
        var storage = await ConnectAsync();
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);
        var device = Assert.IsType<UnwritableSecretDevice>(page.Children["gateway"]);

        // The refused write reads the page again, which applies the configuration of the block again.
        await AsyncTestHelpers.WaitUntilAsync(() => device.ApplyCount == 1);
        await AssertStaysTrueAsync(() => device.ApplyCount == 1);

        // Act
        WriteFile("Gateway.md", $"# Gateway\n\n:: {deviceType}#gateway Password=an0ther-s3cr3t\n::\n");
        await storage.ReconcileAsync(Named("Gateway.md"));

        // Assert: the reload applies the new configuration, and the refused rewrite of it reads the page once more.
        await AsyncTestHelpers.WaitUntilAsync(() => device.ApplyCount == 3);
        await AssertStaysTrueAsync(() => device.ApplyCount == 3);
        Assert.Contains("an0ther-s3cr3t", File.ReadAllText(GetFullPath("Gateway.md")));
    }

    [Fact]
    public async Task WhenThePageFileChangedBeforeTheRewrite_ThenNothingIsWrittenAndTheBlockConfigurationIsAppliedAgain()
    {
        // Arrange: the block cannot be written, so the load's rewrite is refused and reads the page again.
        UseSecretStore();
        var deviceType = typeof(UnwritableSecretDevice).FullName;
        WriteFile("Gateway.md", $"# Gateway\n\n:: {deviceType}#gateway Password=s3cr3t\n::\n");
        var storage = await ConnectAsync();
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);
        var device = Assert.IsType<UnwritableSecretDevice>(page.Children["gateway"]);
        await AsyncTestHelpers.WaitUntilAsync(() => device.ApplyCount == 1);
        var handEdit = $"# Gateway\n\nA hand edit.\n\n:: {deviceType}#gateway Password=s3cr3t\n::\n";
        WriteFile("Gateway.md", handEdit);

        // Act
        await page.RewriteBlocksIfFileUnchangedAsync([device]);

        // Assert: the configuration applied again is what the parse checks for plaintext secrets. Its refused rewrite
        // may already have applied it once more.
        Assert.Equal(handEdit, File.ReadAllText(GetFullPath("Gateway.md")));
        Assert.Equal(handEdit, page.Content);
        Assert.InRange(device.ApplyCount, 2, 3);
    }

    // The load moves the plaintext secret of the block, which leaves its subject marked with the configuration it checked.
    private async Task<(MarkdownFile Page, string MovedContent)> ConnectAndWaitForMovedSecretAsync(string content)
    {
        UseSecretStore();
        WriteFile("Gateway.md", content);
        var storage = await ConnectAsync();
        var movedContent = await WaitForSecretReferenceOnlyAsync("Gateway.md");
        return (Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]), movedContent);
    }

    private string UseSecretStore() => SecretsPath = Path.Combine(CreateTemporaryDirectory().FullName, "Secrets.json");

    [Fact]
    public async Task WhenAParseFailsAfterABlockWithAPlaintextSecretWasUpdated_ThenTheNextParseMovesTheSecret()
    {
        // Arrange
        UseSecretStore();
        var cancelingType = typeof(CancelingDevice).FullName;
        WriteFile("Gateway.md", $"# Gateway\n\n:: {DeviceType}#gateway Host=10.0.0.1\n::\n\n:: {cancelingType}#canceling Value=1\n::\n");
        var storage = await ConnectAsync();
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);
        var canceling = Assert.IsType<CancelingDevice>(page.Children["canceling"]);
        WriteFile("Gateway.md", $"# Gateway\n\n:: {DeviceType}#gateway Host=10.0.0.1 Password=s3cr3t\n::\n\n:: {cancelingType}#canceling Value=2\n::\n");
        using var cancellation = new CancellationTokenSource();
        canceling.CancelOnApply = cancellation;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => page.OnFileChangedAsync(cancellation.Token));
        canceling.CancelOnApply = null;

        // Act
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert
        await WaitForSecretReferenceOnlyAsync("Gateway.md");
    }

    [Fact]
    public async Task WhenAReloadPublishesWhileABlockIsRewritten_ThenThePageIsReadAgainWithTheBlockConfigurationAppliedAgain()
    {
        // Arrange
        var deviceType = typeof(ReloadingDevice).FullName;
        var (page, movedContent) = await ConnectAndWaitForMovedSecretAsync($"# Gateway\n\n:: {deviceType}#gateway Host=10.0.0.1 Password=s3cr3t\n::\n");
        var device = Assert.IsType<ReloadingDevice>(page.Children["gateway"]);
        var applyCount = device.ApplyCount;
        device.Host = "10.0.0.2";

        // The write reads the configuration of the block after it computed its text from the snapshot, and the reload
        // it starts then publishes another snapshot before the write does.
        device.ReloadOnRead = () => Task.Run(() => page.OnFileChangedAsync(CancellationToken.None)).GetAwaiter().GetResult();

        // Act
        await page.RewriteBlocksIfFileUnchangedAsync([device]);

        // Assert
        Assert.Null(device.ReloadOnRead);
        Assert.Equal(applyCount + 1, device.ApplyCount);
        Assert.Equal(movedContent, File.ReadAllText(GetFullPath("Gateway.md")));
    }

    // Only for a check that something does not happen, which a passing test always waits for in full.
    private static async Task AssertStaysTrueAsync(Func<bool> condition)
    {
        await Assert.ThrowsAsync<TimeoutException>(
            () => AsyncTestHelpers.WaitUntilAsync(() => !condition(), timeout: TimeSpan.FromMilliseconds(500)));
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

    // Each check reads the file once: a file that is being rewritten can be read empty, or not at all on Windows.
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

    private bool HoldsSecretReferenceOnly(string relativePath) =>
        TryReadText(relativePath) is { } text && text.Contains("\"password\": \"$secret:") && !text.Contains("s3cr3t");

    private sealed class RecordingLogger(ConcurrentQueue<string> messages) : ILogger<FluentStorageContainer>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => messages.Enqueue(formatter(state, exception));
    }
}

/// <summary>A device whose block can never be written: its slot that takes page content always holds a subject of its own.</summary>
[InterceptorSubject]
public partial class UnwritableSecretDevice : IConfigurable
{
    private readonly EmbeddedSecretDevice _ownChild = new();
    private int _applyCount;

    [Configuration(IsSecret = true)]
    public partial string? Password { get; set; }

    [Slot(IsDefault = true)]
    public partial IInterceptorSubject? Child { get; set; }

    /// <summary>How often the configuration was applied in place.</summary>
    public int ApplyCount => Volatile.Read(ref _applyCount);

    partial void OnChildChanging(ref IInterceptorSubject? newValue, ref bool cancel) => newValue = _ownChild;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _applyCount);
        return Task.CompletedTask;
    }
}

/// <summary>A device whose configuration is read, but throws when it is written: its value cannot be serialized.</summary>
[InterceptorSubject]
public partial class UnserializableSecretDevice : IConfigurable
{
    private int _applyCount;

    [Configuration(IsSecret = true)]
    public partial string? Password { get; set; }

    [Configuration]
    public partial UnserializableValue? Value { get; set; }

    /// <summary>How often the configuration was applied.</summary>
    public int ApplyCount => Volatile.Read(ref _applyCount);

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _applyCount);
        return Task.CompletedTask;
    }
}

/// <summary>A value that is read from JSON and throws when it is written.</summary>
public sealed class UnserializableValue
{
    [Configuration]
    public string? Text
    {
        get => throw new InvalidOperationException("The value cannot be written.");
        set { }
    }
}

/// <summary>A device that cancels the parse that applies its configuration in place while <see cref="CancelOnApply"/> is set.</summary>
[InterceptorSubject]
public partial class CancelingDevice : IConfigurable
{
    [Configuration]
    public partial int Value { get; set; }

    public CancellationTokenSource? CancelOnApply { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        if (CancelOnApply is { } cancellation)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }

        return Task.CompletedTask;
    }
}

/// <summary>A device that runs <see cref="ReloadOnRead"/> once when its configuration is read.</summary>
[InterceptorSubject]
public partial class ReloadingDevice : IConfigurable
{
    private int _applyCount;

    [Configuration]
    public partial string? Host { get; set; }

    [Configuration(IsSecret = true)]
    public partial string? Password { get; set; }

    [Configuration]
    public ReloadingValue Value => new(this);

    public Action? ReloadOnRead { get; set; }

    /// <summary>How often the configuration was applied in place.</summary>
    public int ApplyCount => Volatile.Read(ref _applyCount);

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _applyCount);
        return Task.CompletedTask;
    }
}

/// <summary>The value of a <see cref="ReloadingDevice"/>, which runs its reload when it is written.</summary>
public sealed class ReloadingValue(ReloadingDevice device)
{
    [Configuration]
    public string? Text
    {
        get
        {
            var reload = device.ReloadOnRead;
            device.ReloadOnRead = null;
            reload?.Invoke();
            return null;
        }
    }
}

[InterceptorSubject]
public partial class NestedSecretDevice : IConfigurable
{
    [Configuration(IsSecret = true)]
    public partial string? Password { get; set; }

    [Configuration]
    public partial NestedSecretOptions? Options { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class NestedSecretOptions
{
    [Configuration]
    public string? Endpoint { get; set; }
}
