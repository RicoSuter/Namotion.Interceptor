using System.Collections.Concurrent;
using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services.Secrets;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.Storage.Tests;

public class SecretConversionTests : StorageTestBase
{
    private static readonly string DeviceType = typeof(EmbeddedSecretDevice).FullName!;

    [Fact]
    public async Task WhenAReloadedPlaintextSecretHasAValueThatCannotBeRead_ThenTheSecretIsMovedAndTheValueIsKept()
    {
        // Arrange
        var secretsPath = UseSecretStore();
        WriteFile("Gateway.json", $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.1" }""");
        var messages = new ConcurrentQueue<string>();
        var storage = CreateStorage(logger: new RecordingLogger(messages));
        await storage.ConnectAsync(CancellationToken.None);
        WriteFile("Gateway.json", $$"""{ "$type": "{{DeviceType}}", "host": [1], "password": "s3cr3t" }""");

        // Act
        await storage.ReconcileAsync(Named("Gateway.json"));

        // Assert
        Assert.True(HoldsSecretReferenceOnly("Gateway.json"));
        Assert.Contains("\"host\":[1]", WithoutWhitespace(TryReadText("Gateway.json")!));
        Assert.Contains("s3cr3t", File.ReadAllText(secretsPath));
        Assert.DoesNotContain(messages, message => message.Contains("s3cr3t"));
    }

    [Fact]
    public async Task WhenASecretChangesBetweenTwoSaves_ThenTheFileKeepsItsContentAndTheStoreHoldsOnlyTheLatestValue()
    {
        // Arrange
        UseSecretStore();
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
        Assert.Equal("second", Assert.Single(ReadSecrets()).Value);
    }

    [Fact]
    public async Task WhenTheWriteOfAJsonFileFails_ThenTheChangedSecretIsStored()
    {
        // Arrange
        UseSecretStore();
        var key = SeedSecret("old-password");
        var content = $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.1", "password": "$secret:{{key}}" }""";
        WriteFile("Gateway.json", content);
        PausableStore? client = null;
        var storage = await ConnectAsync(configure: container => container.ClientDecorator = inner => client = new PausableStore(inner));
        var device = Assert.IsType<EmbeddedSecretDevice>(storage.Children["Gateway"]);
        device.Password = "new-password";
        var writeReached = client!.PauseNext(nameof(FluentStorage.Storage.IStore.SetObject));

        // Act
        var save = storage.WriteConfigurationAsync(device, CancellationToken.None);
        await writeReached;
        client.FailHangingCall(new IOException("The share is gone."));
        var failure = await Record.ExceptionAsync(() => save);

        // Assert
        Assert.IsType<IOException>(failure);
        Assert.Equal(new Dictionary<string, string> { [key] = "new-password" }, ReadSecrets());
        Assert.Equal(content, File.ReadAllText(GetFullPath("Gateway.json")));
    }

    [Fact]
    public async Task WhenAPageBlockWriteIsRefusedAfterSerialization_ThenTheChangedSecretStaysInEffect()
    {
        // Arrange
        UseSecretStore();
        var key = SeedSecret("old-password");
        var ownerType = typeof(SecretListOwner).FullName;
        var content = $"# Gateway\n\n:: {ownerType}#owner Host=10.0.0.1 Password=$secret:{key}\n::: Item Host=a\n:::\n::: Item Host=b\n:::\n::\n";
        WriteFile("Gateway.md", content);
        var storage = await ConnectAsync();
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);
        var device = Assert.IsType<SecretListOwner>(page.Children["owner"]);
        device.Host = "10.0.0.2";
        device.Password = "new-password";

        // Refused after the configuration of the block was serialized: the order of the items differs from their blocks.
        device.Items = [device.Items[1], device.Items[0]];

        // Act
        var isWritten = await page.WriteConfigurationAsync(device, CancellationToken.None);

        // Assert
        Assert.False(isWritten);
        Assert.Equal("10.0.0.1", device.Host);
        Assert.Equal("new-password", device.Password);
        Assert.Equal(new Dictionary<string, string> { [key] = "new-password" }, ReadSecrets());
        Assert.Equal(content, File.ReadAllText(GetFullPath("Gateway.md")));
    }

    [Fact]
    public async Task WhenAPageBlockIsSaved_ThenTheChangedSecretIsStoredUnderItsKey()
    {
        // Arrange
        UseSecretStore();
        var key = SeedSecret("old-password");
        WriteFile("Gateway.md", $"# Gateway\n\n:: {DeviceType}#gateway Host=10.0.0.1 Password=$secret:{key}\n::\n");
        var storage = await ConnectAsync();
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);
        var device = Assert.IsType<EmbeddedSecretDevice>(page.Children["gateway"]);
        device.Host = "10.0.0.2";
        device.Password = "new-password";

        // Act
        var isWritten = await page.WriteConfigurationAsync(device, CancellationToken.None);

        // Assert
        Assert.True(isWritten);
        Assert.Equal(new Dictionary<string, string> { [key] = "new-password" }, ReadSecrets());
        Assert.Equal($"# Gateway\n\n:: {DeviceType}#gateway Host=10.0.0.2 Password=$secret:{key}\n::\n", File.ReadAllText(GetFullPath("Gateway.md")));
        Assert.Equal("new-password", device.Password);
    }

    [Fact]
    public async Task WhenAWidgetWithASecretIsInsertedIntoAPage_ThenItsBlockReadsTheSecretFromTheStore()
    {
        // Arrange
        UseSecretStore();
        WriteFile("Gateway.md", "# Gateway\n");
        var storage = await ConnectAsync();
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);

        // Act
        var result = await page.InsertWidgetAsync(
            page, null, new EmbeddedSecretDevice { Host = "10.0.0.1", Password = "s3cr3t" }, "gateway", CancellationToken.None);

        // Assert
        Assert.True(result.IsWritten);
        Assert.Equal("s3cr3t", Assert.IsType<EmbeddedSecretDevice>(page.Children["gateway"]).Password);
        Assert.Equal("s3cr3t", Assert.Single(ReadSecrets()).Value);
        Assert.DoesNotContain("s3cr3t", File.ReadAllText(GetFullPath("Gateway.md")));
    }

    [Fact]
    public async Task WhenASubjectWithASecretIsAdded_ThenItsFileReferencesTheStoredSecret()
    {
        // Arrange
        UseSecretStore();
        var storage = await ConnectAsync();

        // Act
        await storage.AddSubjectAsync("Gateway.json", new EmbeddedSecretDevice { Host = "10.0.0.1", Password = "s3cr3t" }, CancellationToken.None);

        // Assert
        var entry = Assert.Single(ReadSecrets());
        Assert.Equal("s3cr3t", entry.Value);
        Assert.Contains($"\"password\": \"$secret:{entry.Key}\"", File.ReadAllText(GetFullPath("Gateway.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenACopiedJsonFileChangesItsSecret_ThenItGetsItsOwnKeyAndTheOriginalKeepsItsSecret(bool isCopyLoadedFirst)
    {
        // Arrange
        UseSecretStore();
        var key = SeedSecret("shared-password");
        var content = $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.1", "password": "$secret:{{key}}" }""";
        var (firstPath, secondPath) = isCopyLoadedFirst ? ("Copy.json", "Gateway.json") : ("Gateway.json", "Copy.json");
        WriteFile(firstPath, content);
        var storage = await ConnectAsync();
        WriteFile(secondPath, content);
        await storage.ReconcileAsync(Named(secondPath));
        var original = Assert.IsType<EmbeddedSecretDevice>(storage.Children["Gateway"]);
        var copy = Assert.IsType<EmbeddedSecretDevice>(storage.Children["Copy"]);
        copy.Password = "copy-password";

        // Act
        await storage.WriteConfigurationAsync(copy, CancellationToken.None);
        var secretsAfterCopySave = ReadSecrets();
        original.Password = "original-password";
        await storage.WriteConfigurationAsync(original, CancellationToken.None);

        // Assert
        Assert.Equal("shared-password", secretsAfterCopySave[key]);
        Assert.Contains($"\"password\": \"$secret:{key}\"", File.ReadAllText(GetFullPath("Gateway.json")));
        var secrets = ReadSecrets();
        Assert.Equal(2, secrets.Count);
        Assert.Equal("original-password", secrets[key]);
        var copyKey = Assert.Single(secrets.Keys, candidate => candidate != key);
        Assert.Equal("copy-password", secrets[copyKey]);
        Assert.Contains($"\"password\": \"$secret:{copyKey}\"", File.ReadAllText(GetFullPath("Copy.json")));
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
        var content = TryReadText("Gateway.md")!;
        Assert.DoesNotContain("s3cr3t", content);
        Assert.StartsWith($"# Gateway\n\nIntro text.\n\n:: {DeviceType}#gateway Host=10.0.0.1 Password=$secret:", content);
        Assert.EndsWith("::\n\nOutro text.\n", content);
        Assert.Contains("s3cr3t", File.ReadAllText(secretsPath));
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);
        Assert.Equal("s3cr3t", Assert.IsType<EmbeddedSecretDevice>(page.Children["gateway"]).Password);
        Assert.Equal(content, page.Content);
    }

    [Fact]
    public async Task WhenABlockWithAPlaintextSecretHasANestedKeyTheTypeDoesNotKnow_ThenTheSecretIsMovedAndTheKeyIsKept()
    {
        // Arrange
        UseSecretStore();
        var deviceType = typeof(NestedSecretDevice).FullName;
        WriteFile("Gateway.md", $"# Gateway\n\n:: {deviceType}#gateway Password=s3cr3t\n```json\n{{\n  \"Options\": {{ \"Endpoint\": \"a\", \"Typo\": 1 }}\n}}\n```\n::\n");

        // Act
        var storage = await ConnectAsync();

        // Assert
        var content = TryReadText("Gateway.md")!;
        Assert.DoesNotContain("s3cr3t", content);
        Assert.Contains("Password=$secret:", content);
        Assert.Contains("\"Typo\": 1", content);
        Assert.Equal("s3cr3t", Assert.Single(ReadSecrets()).Value);
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);
        Assert.Equal("s3cr3t", Assert.IsType<NestedSecretDevice>(page.Children["gateway"]).Password);
    }

    [Fact]
    public async Task WhenABlockWithAPlaintextSecretHasAValueThatCannotBeRead_ThenTheSecretIsMovedAndTheBlockStaysAnError()
    {
        // Arrange
        var secretsPath = UseSecretStore();
        var errorBlock = $":: {DeviceType}#broken\n```json\n{{\n  \"Host\": [1],\n  \"Password\": \"error-secret\"\n}}\n```\n::\n";
        WriteFile("Gateway.md", $"# Gateway\n\n{errorBlock}\n:: {DeviceType}#gateway Host=10.0.0.1 Password=s3cr3t\n::\n");

        // Act
        var storage = await ConnectAsync();

        // Assert
        var content = TryReadText("Gateway.md")!;
        Assert.DoesNotContain("error-secret", content);
        Assert.DoesNotContain("s3cr3t", content);
        Assert.Contains("\"Host\":[1]", WithoutWhitespace(content));
        Assert.Contains("error-secret", File.ReadAllText(secretsPath));
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);
        Assert.False(page.Children.ContainsKey("broken"));
        Assert.Equal("s3cr3t", Assert.IsType<EmbeddedSecretDevice>(page.Children["gateway"]).Password);
    }

    [Fact]
    public async Task WhenAParseFailsAfterABlockWasUpdatedInPlace_ThenTheNextParseAppliesItsConfigurationAgain()
    {
        // Arrange
        var countingType = typeof(CountingSecretDevice).FullName;
        var cancelingType = typeof(CancelingDevice).FullName;
        WriteFile("Gateway.md", $"# Gateway\n\n:: {countingType}#gateway Host=10.0.0.1\n::\n\n:: {cancelingType}#canceling Value=1\n::\n");
        var storage = await ConnectAsync();
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);
        var gateway = Assert.IsType<CountingSecretDevice>(page.Children["gateway"]);
        var canceling = Assert.IsType<CancelingDevice>(page.Children["canceling"]);
        WriteFile("Gateway.md", $"# Gateway\n\n:: {countingType}#gateway Host=10.0.0.2\n::\n\n:: {cancelingType}#canceling Value=2\n::\n");
        using var cancellation = new CancellationTokenSource();
        canceling.CancelOnApply = cancellation;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => page.OnFileChangedAsync(cancellation.Token));
        canceling.CancelOnApply = null;
        var applyCount = gateway.ApplyCount;

        // Act
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert: the failed parse published nothing, so its update of the block is not taken as applied.
        Assert.Equal("10.0.0.2", gateway.Host);
        Assert.Equal(applyCount + 1, gateway.ApplyCount);
    }

    [Fact]
    public async Task WhenAJsonFileWithAPlaintextSecretIsLoaded_ThenTheFileIsConvertedBeforeTheSubjectIsCreated()
    {
        // Arrange
        UseSecretStore();
        WriteFile("Gateway.json", $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.1", "password": "s3cr3t", "unknownKey": 1 }""");

        // Act
        var storage = await ConnectAsync();

        // Assert: converted synchronously in the load, so no wait is needed
        var text = TryReadText("Gateway.json")!;
        Assert.DoesNotContain("s3cr3t", text);
        Assert.Contains("\"password\": \"$secret:", text);
        Assert.Contains("unknownKey", text);
        Assert.Equal("s3cr3t", Assert.Single(ReadSecrets()).Value);
        Assert.Equal("s3cr3t", Assert.IsType<EmbeddedSecretDevice>(storage.Children["Gateway"]).Password);
    }

    [Fact]
    public async Task WhenAJsonFileWithoutPlaintextIsLoaded_ThenItIsNotWritten()
    {
        // Arrange
        UseSecretStore();
        var original = $$"""{"$type":"{{DeviceType}}","host":"10.0.0.1"}""";
        WriteFile("Gateway.json", original);

        // Act
        await ConnectAsync();

        // Assert
        Assert.Equal(original, TryReadText("Gateway.json"));
    }

    [Fact]
    public async Task WhenAJsonFileWithADuplicateKeyIsLoaded_ThenItsSubjectIsCreated()
    {
        // Arrange
        UseSecretStore();
        WriteFile("Gateway.json", $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.1", "host": "10.0.0.2", "password": "s3cr3t" }""");

        // Act
        var storage = await ConnectAsync();

        // Assert
        Assert.Equal("10.0.0.2", Assert.IsType<EmbeddedSecretDevice>(storage.Children["Gateway"]).Host);
    }

    [Fact]
    public async Task WhenAJsonFileChangesToHoldAPlaintextSecret_ThenTheChangeIsConverted()
    {
        // Arrange
        UseSecretStore();
        WriteFile("Gateway.json", $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.1" }""");
        var storage = await ConnectAsync();
        var device = Assert.IsType<EmbeddedSecretDevice>(storage.Children["Gateway"]);
        WriteFile("Gateway.json", $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.1", "password": "s3cr3t" }""");

        // Act
        await storage.ReconcileAsync(Named("Gateway.json"));

        // Assert
        Assert.Equal("s3cr3t", device.Password);
        Assert.True(HoldsSecretReferenceOnly("Gateway.json"));
        Assert.Equal("s3cr3t", Assert.Single(ReadSecrets()).Value);
    }

    [Fact]
    public async Task WhenAJsonFileWithAPlaintextSecretIsMoved_ThenItIsStillDetectedAsAMove()
    {
        // Arrange
        UseSecretStore();
        WriteFile("A/Gateway.json", $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.1", "password": "s3cr3t" }""");
        var storage = await ConnectAsync();
        var device = Assert.IsType<VirtualFolder>(storage.Children["A"]).Children["Gateway"];
        Directory.CreateDirectory(GetFullPath("B"));
        File.Move(GetFullPath("A/Gateway.json"), GetFullPath("B/Gateway.json"));

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.Same(device, Assert.IsType<VirtualFolder>(storage.Children["B"]).Children["Gateway"]);
    }

    [Fact]
    public async Task WhenAConvertedJsonFileIsReconciledAgain_ThenItsSubjectIsNotReloaded()
    {
        // Arrange
        UseSecretStore();
        var deviceType = typeof(CountingSecretDevice).FullName;
        WriteFile("Gateway.json", $$"""{ "$type": "{{deviceType}}", "host": "10.0.0.1", "password": "s3cr3t" }""");
        var storage = await ConnectAsync();
        var device = Assert.IsType<CountingSecretDevice>(storage.Children["Gateway"]);
        var applyCount = device.ApplyCount;

        // Act: the event of the write-back names the file
        await storage.ReconcileAsync(Named("Gateway.json"));

        // Assert
        Assert.True(HoldsSecretReferenceOnly("Gateway.json"));
        Assert.Same(device, storage.Children["Gateway"]);
        Assert.Equal(applyCount, device.ApplyCount);
    }

    [Fact]
    public async Task WhenAConvertedJsonFileIsEditedLater_ThenItsSubjectIsReloaded()
    {
        // Arrange
        UseSecretStore();
        var deviceType = typeof(CountingSecretDevice).FullName;
        WriteFile("Gateway.json", $$"""{ "$type": "{{deviceType}}", "host": "10.0.0.1", "password": "s3cr3t" }""");
        var storage = await ConnectAsync();
        var device = Assert.IsType<CountingSecretDevice>(storage.Children["Gateway"]);
        var applyCount = device.ApplyCount;
        WriteFile("Gateway.json", TryReadText("Gateway.json")!.Replace("10.0.0.1", "10.0.0.2"));

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal("10.0.0.2", device.Host);
        Assert.Equal("s3cr3t", device.Password);
        Assert.Equal(applyCount + 1, device.ApplyCount);
    }

    [Fact]
    public async Task WhenAUtf8JsonFileWithAByteOrderMarkIsConverted_ThenItKeepsTheByteOrderMark()
    {
        // Arrange
        UseSecretStore();
        File.WriteAllText(
            GetFullPath("Gateway.json"),
            $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.1", "password": "s3cr3t" }""",
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        // Act
        await ConnectAsync();

        // Assert
        var content = File.ReadAllBytes(GetFullPath("Gateway.json"));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, content[..3]);
        Assert.NotEqual(0xEF, content[3]);
        Assert.True(HoldsSecretReferenceOnly("Gateway.json"));
    }

    [Fact]
    public async Task WhenAUtf16JsonFileIsConverted_ThenItStaysUtf16()
    {
        // Arrange
        UseSecretStore();
        File.WriteAllText(
            GetFullPath("Gateway.json"),
            $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.1", "password": "s3cr3t" }""",
            System.Text.Encoding.Unicode);

        // Act
        await ConnectAsync();

        // Assert
        var content = File.ReadAllBytes(GetFullPath("Gateway.json"));
        Assert.Equal(System.Text.Encoding.Unicode.GetPreamble(), content[..2]);
        Assert.Contains("\"password\": \"$secret:", System.Text.Encoding.Unicode.GetString(content, 2, content.Length - 2));
        Assert.Equal("s3cr3t", Assert.Single(ReadSecrets()).Value);
    }

    [Fact]
    public async Task WhenAPageIsNotValidUtf8_ThenItIsNotConvertedAndKeepsItsBytes()
    {
        // Arrange: "Küche" in Windows-1252, whose 0xFC is not valid UTF-8.
        UseSecretStore();
        var content = System.Text.Encoding.Latin1.GetBytes($"# Küche\n\n:: {DeviceType}#gateway Password=s3cr3t\n::\n");
        File.WriteAllBytes(GetFullPath("Kitchen.md"), content);
        var messages = new ConcurrentQueue<string>();
        var storage = CreateStorage(logger: new RecordingLogger(messages));

        // Act
        await storage.ConnectAsync(CancellationToken.None);

        // Assert
        Assert.Equal(content, File.ReadAllBytes(GetFullPath("Kitchen.md")));
        Assert.Empty(ReadSecrets());
        Assert.Contains(messages, message => message.Contains("Kitchen.md") && message.Contains("not valid"));
    }

    [Fact]
    public async Task WhenAJsonFileThatIsNotValidUtf8IsWrittenThroughTheStorage_ThenItIsWrittenAsItIs()
    {
        // Arrange
        UseSecretStore();
        var storage = await ConnectAsync();
        var content = System.Text.Encoding.Latin1.GetBytes(
            $$"""{ "$type": "{{DeviceType}}", "host": "Küche", "password": "s3cr3t" }""");

        // Act
        await storage.WriteBlobAsync("Gateway.json", content, CancellationToken.None);

        // Assert
        Assert.Equal(content, File.ReadAllBytes(GetFullPath("Gateway.json")));
        Assert.Empty(ReadSecrets());
    }

    [Fact]
    public async Task WhenAJsonFileChangesWhileItIsConverted_ThenTheEditIsKeptAndReadByAnotherPass()
    {
        // Arrange
        UseSecretStore();
        WriteFile("Gateway.json", $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.1" }""");
        PausableStore? client = null;
        var storage = await ConnectAsync(configure: container => container.ClientDecorator = inner => client = new PausableStore(inner));
        var device = Assert.IsType<EmbeddedSecretDevice>(storage.Children["Gateway"]);
        WriteFile("Gateway.json", $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.1", "password": "s3cr3t" }""");
        var handEdit = $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.9" }""";

        // The second read is the one that checks the file before the converted content is written back.
        client!.RunBeforeOpenRead(2, () => WriteFile("Gateway.json", handEdit));

        // Act
        await storage.ReconcileAsync(Named("Gateway.json"));

        // Assert
        Assert.Equal(handEdit, TryReadText("Gateway.json"));
        await AsyncTestHelpers.WaitUntilAsync(() => device.Host == "10.0.0.9");
        Assert.Equal(handEdit, TryReadText("Gateway.json"));
    }

    [Fact]
    public async Task WhenTheWriteBackOfAConvertedJsonFileFails_ThenTheReadContentIsAppliedAndAWarningIsLogged()
    {
        // Arrange
        UseSecretStore();
        WriteFile("Gateway.json", $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.1" }""");
        PausableStore? client = null;
        var messages = new ConcurrentQueue<string>();
        var storage = CreateStorage(configure: container => container.ClientDecorator = inner => client = new PausableStore(inner), logger: new RecordingLogger(messages));
        await storage.ConnectAsync(CancellationToken.None);
        var device = Assert.IsType<EmbeddedSecretDevice>(storage.Children["Gateway"]);
        WriteFile("Gateway.json", $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.2", "password": "s3cr3t" }""");
        var writeReached = client!.PauseNext(nameof(FluentStorage.Storage.IStore.SetObject));

        // Act
        var reconcile = storage.ReconcileAsync(Named("Gateway.json"));
        await writeReached;
        client.FailHangingCall(new IOException("The share is gone."));
        await reconcile;

        // Assert
        Assert.Equal("10.0.0.2", device.Host);
        Assert.Equal("s3cr3t", device.Password);
        Assert.Contains(messages, message => message.Contains("Gateway.json") && message.Contains("could not be written back"));
    }

    [Fact]
    public async Task WhenAMarkdownLongPageWithAPlaintextSecretIsLoaded_ThenTheFileIsConverted()
    {
        // Arrange
        UseSecretStore();
        WriteFile("Gateway.markdown", $"# Gateway\n\n:: {DeviceType}#gateway Host=10.0.0.1 Password=s3cr3t\n::\n");

        // Act
        var storage = await ConnectAsync();

        // Assert
        var text = TryReadText("Gateway.markdown")!;
        Assert.DoesNotContain("s3cr3t", text);
        Assert.Contains("Password=$secret:", text);
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.markdown"]);
        Assert.Equal("s3cr3t", ((EmbeddedSecretDevice)page.Children["gateway"]).Password);
    }

    [Fact]
    public async Task WhenAPageChangesToHoldAPlaintextSecret_ThenTheChangeIsConverted()
    {
        // Arrange
        UseSecretStore();
        WriteFile("Gateway.md", $"# Gateway\n\n:: {DeviceType}#gateway Host=10.0.0.1\n::\n");
        var storage = await ConnectAsync();
        WriteFile("Gateway.md", $"# Gateway\n\n:: {DeviceType}#gateway Host=10.0.0.1 Password=s3cr3t\n::\n");

        // Act
        await storage.ReconcileAsync(Named("Gateway.md"));

        // Assert
        var text = TryReadText("Gateway.md")!;
        Assert.DoesNotContain("s3cr3t", text);
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);
        Assert.Equal("s3cr3t", ((EmbeddedSecretDevice)page.Children["gateway"]).Password);
        Assert.Equal(text, page.Content);
    }

    [Fact]
    public async Task WhenAConvertedPageIsReconciledAgain_ThenItsBlockIsNotAppliedAgain()
    {
        // Arrange
        UseSecretStore();
        var deviceType = typeof(CountingSecretDevice).FullName;
        WriteFile("Gateway.md", $"# Gateway\n\n:: {deviceType}#gateway Host=10.0.0.1 Password=s3cr3t\n::\n");
        var storage = await ConnectAsync();
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);
        var device = Assert.IsType<CountingSecretDevice>(page.Children["gateway"]);
        var applyCount = device.ApplyCount;

        // Act: the event of the write-back names the file
        await storage.ReconcileAsync(Named("Gateway.md"));

        // Assert
        Assert.DoesNotContain("s3cr3t", TryReadText("Gateway.md"));
        Assert.Same(device, page.Children["gateway"]);
        Assert.Equal(applyCount, device.ApplyCount);
    }

    [Fact]
    public async Task WhenAPageWithoutPlaintextIsLoaded_ThenItIsNotWritten()
    {
        // Arrange
        UseSecretStore();
        var original = $"# Gateway\n\n:: {DeviceType}#gateway Host=10.0.0.1\n::\n";
        WriteFile("Gateway.md", original);
        PausableStore? client = null;
        var storage = CreateStorage(configure: container => container.ClientDecorator = inner => client = new PausableStore(inner));

        // Act
        await storage.ConnectAsync(CancellationToken.None);

        // Assert
        Assert.Equal(original, TryReadText("Gateway.md"));
        Assert.Equal(0, client!.GetCallCount(nameof(FluentStorage.Storage.IStore.SetObject)));
    }

    [Fact]
    public async Task WhenAPageHasMalformedBlocks_ThenItIsLoadedAndItsValidBlockIsConverted()
    {
        // Arrange
        UseSecretStore();
        var malformedBlocks =
            $":: {DeviceType}#invalidJson\n```json\n{{ \"Password\": \"first\", \n```\n::\n\n" +
            $":: {DeviceType}#duplicateKey\n```json\n{{ \"Password\": \"second\", \"Password\": \"third\" }}\n```\n::\n\n" +
            $":: {DeviceType}#array\n```json\n[ {{ \"Password\": \"fourth\" }} ]\n```\n::\n\n" +
            $":: {DeviceType}#openQuote Password=\"fifth\n::\n\n" +
            ":: Unknown.Type#unknown Password=sixth\n::\n\n" +
            ":: notAType Password=seventh\n::\n\n" +
            ":: \n::\n\n";
        WriteFile("Gateway.md", $"# Gateway\n\n{malformedBlocks}:: {DeviceType}#gateway Host=10.0.0.1 Password=s3cr3t\n::\n");

        // Act
        var storage = await ConnectAsync();

        // Assert
        var text = TryReadText("Gateway.md")!;
        Assert.StartsWith($"# Gateway\n\n{malformedBlocks}:: {DeviceType}#gateway Host=10.0.0.1 Password=$secret:", text);
        Assert.DoesNotContain("s3cr3t", text);
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);
        Assert.Equal("s3cr3t", ((EmbeddedSecretDevice)page.Children["gateway"]).Password);
        Assert.Equal(text, page.Content);
    }

    [Fact]
    public async Task WhenAJsonFileWithAPlaintextSecretIsWrittenThroughTheStorage_ThenTheFileOnlyEverHoldsAReference()
    {
        // Arrange
        UseSecretStore();
        var storage = await ConnectAsync();
        var json = $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.1", "password": "s3cr3t" }""";

        // Act
        await storage.WriteBlobAsync("Gateway.json", System.Text.Encoding.UTF8.GetBytes(json), CancellationToken.None);

        // Assert
        Assert.True(HoldsSecretReferenceOnly("Gateway.json"));
        Assert.Contains("s3cr3t", File.ReadAllText(SecretsPath!));
    }

    [Fact]
    public async Task WhenAPageWithAPlaintextSecretIsWrittenThroughTheStorageAsAStream_ThenTheFileOnlyEverHoldsAReference()
    {
        // Arrange
        UseSecretStore();
        WriteFile("Gateway.md", "# Gateway\n");
        var storage = await ConnectAsync();

        // Act
        await WriteBlobAsync(storage, "Gateway.md", $"# Gateway\n\n:: {DeviceType}#gateway Password=s3cr3t\n::\n");

        // Assert
        var text = TryReadText("Gateway.md")!;
        Assert.DoesNotContain("s3cr3t", text);
        Assert.Contains("Password=$secret:", text);
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);
        Assert.Equal("s3cr3t", ((EmbeddedSecretDevice)page.Children["gateway"]).Password);
    }

    [Fact]
    public async Task WhenTheSecretStoreCannotBeWrittenDuringAWrite_ThenTheWriteFailsAndTheFileIsUnchanged()
    {
        // Arrange
        UseSecretStore();
        WriteFile("Gateway.json", $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.1" }""");
        var storage = await ConnectAsync();
        Directory.CreateDirectory(SecretsPath!);
        var json = $$"""{ "$type": "{{DeviceType}}", "host": "10.0.0.1", "password": "s3cr3t" }""";

        // Act & Assert
        await Assert.ThrowsAnyAsync<Exception>(() => storage.WriteBlobAsync("Gateway.json", System.Text.Encoding.UTF8.GetBytes(json), CancellationToken.None));
        Assert.DoesNotContain("s3cr3t", TryReadText("Gateway.json"));
    }

    [Fact]
    public async Task WhenAPageSourceWithAPlaintextSecretIsWritten_ThenTheWrittenTextIsReturnedWithAReference()
    {
        // Arrange
        UseSecretStore();
        WriteFile("Gateway.md", "# Gateway\n");
        var storage = await ConnectAsync();
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);

        // Act
        var written = await page.WriteAsync($"# Gateway\n\n:: {DeviceType}#gateway Password=s3cr3t\n::\n", CancellationToken.None);

        // Assert
        Assert.DoesNotContain("s3cr3t", written);
        Assert.Contains("Password=$secret:", written);
        Assert.Equal(written, TryReadText("Gateway.md"));
        Assert.Equal(written, page.Content);
        Assert.Equal("s3cr3t", ((EmbeddedSecretDevice)page.Children["gateway"]).Password);
    }

    [Fact]
    public async Task WhenATextBlockIsReplacedWithAWidgetWithAPlaintextSecret_ThenTheFileOnlyEverHoldsAReference()
    {
        // Arrange
        UseSecretStore();
        WriteFile("Gateway.md", "# Gateway\n\nOld text.\n");
        var storage = await ConnectAsync();
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);
        var segment = page.Children.Values.OfType<HtmlSegment>()
            .Single(candidate => candidate.Start == HtmlSegmentStart.Text && candidate.Html.Contains("Old text."));

        // Act
        var result = await page.ReplaceTextBlockAsync(
            segment, "Old text.", $"New text.\n\n:: {DeviceType}#gateway Password=s3cr3t\n::", CancellationToken.None);

        // Assert
        Assert.True(result.IsWritten);
        AssertPageHoldsSecretReferenceOnly(page, "Gateway.md");
    }

    [Fact]
    public async Task WhenTextWithAWidgetWithAPlaintextSecretIsInserted_ThenTheFileOnlyEverHoldsAReference()
    {
        // Arrange
        UseSecretStore();
        WriteFile("Gateway.md", "# Gateway\n");
        var storage = await ConnectAsync();
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);

        // Act
        var result = await page.InsertTextAsync(
            page, null, $"Text.\n\n:: {DeviceType}#gateway Password=s3cr3t\n::", CancellationToken.None);

        // Assert
        Assert.True(result.IsWritten);
        AssertPageHoldsSecretReferenceOnly(page, "Gateway.md");
    }

    [Fact]
    public async Task WhenTextWithAWidgetWithAPlaintextSecretIsAddedToASlot_ThenTheFileOnlyEverHoldsAReference()
    {
        // Arrange
        UseSecretStore();
        WriteFile("Gateway.md", $"# Gateway\n\n:: {typeof(Pages.TestPanel).FullName}#panel Title=P\n::\n");
        var storage = await ConnectAsync();
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);
        var panel = page.Children["panel"];

        // Act
        var result = await page.AddTextToSlotAsync(
            panel, null, $"Text.\n\n:: {DeviceType}#gateway Password=s3cr3t\n::", CancellationToken.None);

        // Assert
        Assert.True(result.IsWritten);
        var text = TryReadText("Gateway.md")!;
        Assert.Contains($"::: {DeviceType}#gateway Password=$secret:", text);
        Assert.DoesNotContain("s3cr3t", text);
        Assert.Equal("s3cr3t", Assert.Single(ReadSecrets()).Value);
    }

    [Fact]
    public async Task WhenAPageSourceWithoutPlaintextIsWritten_ThenTheGivenTextIsReturned()
    {
        // Arrange
        UseSecretStore();
        WriteFile("Gateway.md", "# Gateway\n");
        var storage = await ConnectAsync();
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);
        var content = $"# Gateway\n\n:: {DeviceType}#gateway Host=10.0.0.1\n::\n";

        // Act
        var written = await page.WriteAsync(content, CancellationToken.None);

        // Assert
        Assert.Same(content, written);
        Assert.Equal(content, TryReadText("Gateway.md"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenTextIsReplacedInAPageThatAnExternalEditGaveAPlaintextSecret_ThenTheFileHoldsAReference(bool replacesAll)
    {
        // Arrange
        UseSecretStore();
        WriteFile("Gateway.md", "# Gateway\n");
        var storage = await ConnectAsync();
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);
        WriteFile("Gateway.md", $"# Gateway\n\n:: {DeviceType}#gateway Password=s3cr3t\n::\n");

        // Act
        if (replacesAll)
        {
            await page.ReplaceContentAsync($"# Gate\n\n:: {DeviceType}#gateway Password=***\n::\n", CancellationToken.None);
        }
        else
        {
            await page.ReplaceTextAsync("# Gateway", "# Gate", CancellationToken.None);
        }

        // Assert
        var text = TryReadText("Gateway.md")!;
        Assert.StartsWith("# Gate\n", text);
        Assert.DoesNotContain("s3cr3t", text);
        Assert.Contains("Password=$secret:", text);
    }

    [Fact]
    public async Task WhenTheSecretStoreCannotBeWrittenDuringAPageSourceWrite_ThenTheWriteFailsAndTheFileIsUnchanged()
    {
        // Arrange
        UseSecretStore();
        WriteFile("Gateway.md", "# Gateway\n");
        var storage = await ConnectAsync();
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);
        Directory.CreateDirectory(SecretsPath!);

        // Act & Assert
        await Assert.ThrowsAnyAsync<Exception>(() => page.WriteAsync($"# Gateway\n\n:: {DeviceType}#gateway Password=s3cr3t\n::\n", CancellationToken.None));
        Assert.Equal("# Gateway\n", TryReadText("Gateway.md"));
    }

    [Fact]
    public async Task WhenAStructureEditIsRetriedAfterARefusalReadAPlaintextSecret_ThenTheFileHoldsAReferenceForGood()
    {
        // Arrange
        UseSecretStore();
        WriteFile("Gateway.md", "# Gateway\n");
        var storage = await ConnectAsync();
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);
        WriteFile("Gateway.md", $"# Gateway\n\n:: {DeviceType}#gateway Password=s3cr3t\n::\n");
        var refused = await page.InsertTextAsync(page, null, "Text one.", CancellationToken.None);

        // Act
        var result = await page.InsertTextAsync(page, null, "Text two.", CancellationToken.None);
        await storage.ReconcileAsync();
        await storage.ReconcileAsync();

        // Assert
        Assert.False(refused.IsWritten);
        Assert.True(result.IsWritten);
        Assert.Contains("Text two.", TryReadText("Gateway.md"));
        AssertPageHoldsSecretReferenceOnly(page, "Gateway.md");
    }

    [Fact]
    public async Task WhenABlockIsSavedAfterARefusalReadAPlaintextSecret_ThenTheFileHoldsAReferenceForGood()
    {
        // Arrange
        UseSecretStore();
        WriteFile("Gateway.md", $"# Gateway\n\n:: {DeviceType}#other Host=10.0.0.1\n::\n");
        var storage = await ConnectAsync();
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);
        WriteFile("Gateway.md", $"# Gateway\n\n:: {DeviceType}#other Host=10.0.0.1\n::\n\n:: {DeviceType}#gateway Password=s3cr3t\n::\n");
        var refused = await page.InsertTextAsync(page, null, "Text.", CancellationToken.None);
        var other = Assert.IsType<EmbeddedSecretDevice>(page.Children["other"]);
        other.Host = "10.0.0.2";

        // Act
        var isWritten = await page.WriteConfigurationAsync(other, CancellationToken.None);
        await storage.ReconcileAsync();
        await storage.ReconcileAsync();

        // Assert
        Assert.False(refused.IsWritten);
        Assert.True(isWritten);
        Assert.Contains("Host=10.0.0.2", TryReadText("Gateway.md"));
        AssertPageHoldsSecretReferenceOnly(page, "Gateway.md");
    }

    [Fact]
    public async Task WhenTheSecretStoreCannotBeWrittenDuringABlockWrite_ThenTheExceptionPropagatesAndTheBlockTakesTheFileAgain()
    {
        // Arrange
        UseSecretStore();
        var ownerType = typeof(SecretListOwner).FullName;
        var content = $"# Gateway\n\n:: {ownerType}#owner Host=10.0.0.1\n::: Item Host=a\n:::\n::\n";
        WriteFile("Gateway.md", content);
        var storage = await ConnectAsync();
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);
        var owner = Assert.IsType<SecretListOwner>(page.Children["owner"]);
        Directory.CreateDirectory(SecretsPath!);
        owner.Host = "10.0.0.2";
        owner.Items = [owner.Items[0], new EmbeddedSecretDevice { Host = "b", Password = "s3cr3t" }];

        // Act
        var failure = await Record.ExceptionAsync(() => page.WriteConfigurationAsync(owner, CancellationToken.None));

        // Assert
        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        Assert.Equal("10.0.0.1", owner.Host);
        Assert.Equal("a", Assert.Single(owner.Items).Host);
        Assert.Equal(content, page.Content);
        Assert.Equal(content, TryReadText("Gateway.md"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenThePageHoldsAPlaintextSecretAndTheSecretStoreCannotBeWritten_ThenABlockWriteOrStructureEditThrowsAndTheFileIsUnchanged(bool isStructureEdit)
    {
        // Arrange
        UseSecretStore();
        WriteFile("Gateway.md", $"# Gateway\n\n:: {DeviceType}#other Host=10.0.0.1\n::\n");
        var storage = await ConnectAsync();
        var page = Assert.IsType<MarkdownFile>(storage.Children["Gateway.md"]);
        var content = $"# Gateway\n\n:: {DeviceType}#other Host=10.0.0.1\n::\n\n:: {DeviceType}#gateway Password=s3cr3t\n::\n";
        WriteFile("Gateway.md", content);

        // The read after the refusal takes the plaintext in, which only the storage converts.
        var refused = await page.InsertTextAsync(page, null, "Text.", CancellationToken.None);
        Directory.CreateDirectory(SecretsPath!);
        var other = Assert.IsType<EmbeddedSecretDevice>(page.Children["other"]);
        other.Host = "10.0.0.2";

        // Act
        var failure = await Record.ExceptionAsync(() => isStructureEdit
            ? page.InsertTextAsync(page, null, "Text.", CancellationToken.None)
            : page.WriteConfigurationAsync(other, CancellationToken.None));

        // Assert
        Assert.False(refused.IsWritten);
        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        Assert.Equal(content, TryReadText("Gateway.md"));
        Assert.Equal(content, page.Content);
    }

    [Fact]
    public async Task WhenAMetadataFileIsWritten_ThenItIsNotConverted()
    {
        // Arrange
        UseSecretStore();
        var storage = await ConnectAsync();
        var json = $$"""{ "$type": "{{DeviceType}}", "password": "s3cr3t" }""";

        // Act
        await storage.WriteBlobAsync("Gateway.metadata.json", System.Text.Encoding.UTF8.GetBytes(json), CancellationToken.None);

        // Assert
        Assert.Equal(json, TryReadText("Gateway.metadata.json"));
    }

    private string UseSecretStore() => SecretsPath = Path.Combine(CreateTemporaryDirectory().FullName, "Secrets.json");

    // Through a store of its own, before the storage creates the one it reads.
    private string SeedSecret(string value)
    {
        var key = Guid.NewGuid().ToString("N");
        new SecretStore(SecretsPath!).Set(key, value);
        return key;
    }

    private Dictionary<string, string> ReadSecrets() => File.Exists(SecretsPath)
        ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SecretsPath))!
        : [];

    // The conversion writes a block or file it changes indented, so values are compared without the layout.
    private static string WithoutWhitespace(string text) => string.Concat(text.Where(character => !char.IsWhiteSpace(character)));

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

    private void AssertPageHoldsSecretReferenceOnly(MarkdownFile page, string relativePath)
    {
        var text = TryReadText(relativePath)!;
        Assert.Contains($":: {DeviceType}#gateway Password=$secret:", text);
        Assert.DoesNotContain("s3cr3t", text);
        Assert.Equal(text, page.Content);
        Assert.Equal("s3cr3t", ((EmbeddedSecretDevice)page.Children["gateway"]).Password);
        Assert.Equal("s3cr3t", Assert.Single(ReadSecrets()).Value);
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

/// <summary>A device with a secret that counts how often its configuration was applied in place.</summary>
[InterceptorSubject]
public partial class CountingSecretDevice : IConfigurable
{
    private int _applyCount;

    [Configuration]
    public partial string? Host { get; set; }

    [Configuration(IsSecret = true)]
    public partial string? Password { get; set; }

    /// <summary>How often the configuration was applied in place.</summary>
    public int ApplyCount => Volatile.Read(ref _applyCount);

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _applyCount);
        return Task.CompletedTask;
    }
}

/// <summary>A device with a secret and a list slot, whose block is refused when its items change their order.</summary>
[InterceptorSubject]
public partial class SecretListOwner : IConfigurable
{
    public SecretListOwner()
    {
        Items = [];
    }

    [Configuration]
    public partial string? Host { get; set; }

    [Configuration(IsSecret = true)]
    public partial string? Password { get; set; }

    [Slot("Item")]
    public partial EmbeddedSecretDevice[] Items { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
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
