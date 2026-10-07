using HomeBlaze.Samples;
using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Storage.Tests;

public class FluentStorageContainerRescanTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("homeblaze-rescan-");
    private readonly DirectoryInfo _otherDirectory = Directory.CreateTempSubdirectory("homeblaze-rescan-other-");
    private readonly CapturingLogger<FluentStorageContainer> _logger = new();

    [Fact]
    public async Task WhenWatcherErrorTriggersRescan_ThenUnchangedSubjectsAreKept()
    {
        // Arrange
        WriteUnchangedFiles();
        using var storage = CreateStorage(enableFileWatching: true);
        await storage.ConnectAsync(CancellationToken.None);
        var before = Snapshot(storage);

        // Act
        storage.FileWatcher!.SimulateWatcherError(new InternalBufferOverflowException());
        await AsyncTestHelpers.WaitUntilAsync(() => _logger.CountMessages("Scan complete") == 2);

        // Assert
        AssertUnchanged(before, storage);
    }

    [Fact]
    public async Task WhenConfigurationIsAppliedWithoutChanges_ThenUnchangedSubjectsAreKept()
    {
        // Arrange
        WriteUnchangedFiles();
        using var storage = CreateStorage();
        await storage.ConnectAsync(CancellationToken.None);
        var before = Snapshot(storage);

        // Act
        await storage.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        AssertUnchanged(before, storage);
    }

    [Fact]
    public async Task WhenConfigurableJsonChangedBeforeRescan_ThenSameSubjectIsReconfigured()
    {
        // Arrange
        WriteFile("Counter.json", CounterJson("first"));
        WriteFile("notes.txt", "notes");
        using var storage = CreateStorage();
        await storage.ConnectAsync(CancellationToken.None);
        var counter = Assert.IsType<CountingConfigurableSubject>(storage.Children["Counter"]);
        var notes = storage.Children["notes.txt"];

        // Act
        WriteFile("Counter.json", CounterJson("second"));
        await storage.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Same(counter, storage.Children["Counter"]);
        Assert.Equal("second", counter.Value);
        Assert.Equal(1, counter.ApplyCount);
        Assert.Same(notes, storage.Children["notes.txt"]);
    }

    [Fact]
    public async Task WhenConfigurableJsonIsUnchangedAtRescan_ThenItIsNotReconfigured()
    {
        // Arrange
        WriteFile("Counter.json", CounterJson("first"));
        using var storage = CreateStorage();
        await storage.ConnectAsync(CancellationToken.None);
        var counter = Assert.IsType<CountingConfigurableSubject>(storage.Children["Counter"]);

        // Act
        await storage.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Same(counter, storage.Children["Counter"]);
        Assert.Equal(0, counter.ApplyCount);
    }

    [Fact]
    public async Task WhenJsonTypeChangedBeforeRescan_ThenSubjectIsRecreatedWithTheNewType()
    {
        // Arrange
        WriteFile("Device.json", CounterJson("first"));
        using var storage = CreateStorage();
        await storage.ConnectAsync(CancellationToken.None);
        Assert.IsType<CountingConfigurableSubject>(storage.Children["Device"]);

        // Act
        WriteFile("Device.json", """{ "$type": "HomeBlaze.Samples.Motor", "name": "Motor" }""");
        await storage.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        var motor = Assert.IsType<Motor>(storage.Children["Device"]);
        Assert.Equal("Motor", motor.Name);
    }

    [Fact]
    public async Task WhenDocumentsChangedBeforeRescan_ThenTheyAreRefreshedInPlace()
    {
        // Arrange
        WriteFile("Readme.md", "# First");
        WriteFile("notes.txt", "notes");
        using var storage = CreateStorage();
        await storage.ConnectAsync(CancellationToken.None);
        var markdownFile = Assert.IsType<MarkdownFile>(storage.Children["Readme.md"]);
        var genericFile = Assert.IsType<GenericFile>(storage.Children["notes.txt"]);

        // Act
        WriteFile("Readme.md", "# Second");
        WriteFile("notes.txt", "longer notes");
        await storage.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Same(markdownFile, storage.Children["Readme.md"]);
        Assert.Equal("# Second", markdownFile.Content);
        Assert.Same(genericFile, storage.Children["notes.txt"]);
        Assert.Equal("longer notes".Length, genericFile.FileSize);
    }

    [Fact]
    public async Task WhenFilesAreAddedAndDeletedBeforeRescan_ThenHierarchyFollows()
    {
        // Arrange
        WriteFile("kept.txt", "kept");
        WriteFile("deleted.txt", "deleted");
        WriteFile("Old/inner.txt", "inner");
        WriteFile("Devices/Motor1.json", """{ "$type": "HomeBlaze.Samples.Motor" }""");
        using var storage = CreateStorage();
        await storage.ConnectAsync(CancellationToken.None);
        var kept = storage.Children["kept.txt"];
        var devices = Assert.IsType<VirtualFolder>(storage.Children["Devices"]);
        var motor1 = Assert.IsType<Motor>(devices.Children["Motor1"]);
        var deleted = storage.Children["deleted.txt"];

        // Act
        File.Delete(Path.Combine(_directory.FullName, "deleted.txt"));
        Directory.Delete(Path.Combine(_directory.FullName, "Old"), recursive: true);
        WriteFile("added.txt", "added");
        WriteFile("Devices/Motor2.json", """{ "$type": "HomeBlaze.Samples.Motor" }""");
        await storage.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(["Devices", "added.txt", "kept.txt"], storage.Children.Keys.Order(StringComparer.Ordinal));
        Assert.Same(kept, storage.Children["kept.txt"]);
        Assert.IsType<GenericFile>(storage.Children["added.txt"]);
        Assert.Same(devices, storage.Children["Devices"]);
        Assert.Same(motor1, devices.Children["Motor1"]);
        Assert.IsType<Motor>(devices.Children["Motor2"]);

        // The deleted file's subject is no longer the storage's: deleting it again finds nothing.
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.DeleteSubjectAsync(deleted, CancellationToken.None));
    }

    [Fact]
    public async Task WhenStorageDirectoryChanged_ThenRescanRebuildsEverySubject()
    {
        // Arrange
        WriteFile("notes.txt", "notes");
        File.WriteAllText(Path.Combine(_otherDirectory.FullName, "notes.txt"), "notes");
        using var storage = CreateStorage();
        await storage.ConnectAsync(CancellationToken.None);
        var notes = storage.Children["notes.txt"];

        // Act
        storage.ConnectionString = _otherDirectory.FullName;
        await storage.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        var otherNotes = Assert.IsType<GenericFile>(storage.Children["notes.txt"]);
        Assert.NotSame(notes, otherNotes);
    }

    [Fact]
    public async Task WhenPlaceholderFileGetsKnownTypeBeforeRescan_ThenPlaceholderIsUpgraded()
    {
        // Arrange
        WriteFile("Devices/Motor1.json", """{ "$type": "HomeBlaze.Samples.Motorr" }""");
        using var storage = CreateStorage();
        await storage.ConnectAsync(CancellationToken.None);
        var devices = Assert.IsType<VirtualFolder>(storage.Children["Devices"]);
        Assert.IsType<UnknownSubject>(devices.Children["Motor1"]);

        // Act
        WriteFile("Devices/Motor1.json", """{ "$type": "HomeBlaze.Samples.Motor" }""");
        await storage.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Same(devices, storage.Children["Devices"]);
        Assert.IsType<Motor>(devices.Children["Motor1"]);
    }

    private void WriteUnchangedFiles()
    {
        WriteFile("Counter.json", CounterJson("first"));
        WriteFile("Readme.md", "# Readme");
        WriteFile("notes.txt", "notes");
        WriteFile("Devices/Motor1.json", """{ "$type": "HomeBlaze.Samples.Motor" }""");
    }

    private static StorageSnapshot Snapshot(FluentStorageContainer storage)
    {
        var devices = Assert.IsType<VirtualFolder>(storage.Children["Devices"]);
        var markdownFile = Assert.IsType<MarkdownFile>(storage.Children["Readme.md"]);
        return new StorageSnapshot(
            storage.Children,
            devices,
            devices.Children,
            Assert.IsType<CountingConfigurableSubject>(storage.Children["Counter"]),
            markdownFile,
            markdownFile.Children,
            Assert.IsType<GenericFile>(storage.Children["notes.txt"]),
            Assert.IsType<Motor>(devices.Children["Motor1"]));
    }

    private static void AssertUnchanged(StorageSnapshot before, FluentStorageContainer storage)
    {
        Assert.Same(before.Children, storage.Children);
        Assert.Same(before.Devices, storage.Children["Devices"]);
        Assert.Same(before.DevicesChildren, before.Devices.Children);
        Assert.Same(before.Counter, storage.Children["Counter"]);
        Assert.Same(before.MarkdownFile, storage.Children["Readme.md"]);
        Assert.Same(before.GenericFile, storage.Children["notes.txt"]);
        Assert.Same(before.Motor, before.Devices.Children["Motor1"]);

        // Neither reconfigured nor parsed again.
        Assert.Equal(0, before.Counter.ApplyCount);
        Assert.Same(before.MarkdownChildren, before.MarkdownFile.Children);
    }

    private sealed record StorageSnapshot(
        Dictionary<string, IInterceptorSubject> Children,
        VirtualFolder Devices,
        Dictionary<string, IInterceptorSubject> DevicesChildren,
        CountingConfigurableSubject Counter,
        MarkdownFile MarkdownFile,
        IDictionary<string, IInterceptorSubject> MarkdownChildren,
        GenericFile GenericFile,
        Motor Motor);

    private static string CounterJson(string value) =>
        $$"""{ "$type": "{{typeof(CountingConfigurableSubject).FullName}}", "value": "{{value}}" }""";

    private void WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_directory.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private FluentStorageContainer CreateStorage(bool enableFileWatching = false)
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddAssembly(typeof(MarkdownFile).Assembly);
        typeProvider.AddAssembly(typeof(Motor).Assembly);
        typeProvider.AddTypes([typeof(CountingConfigurableSubject)]);
        var typeRegistry = new SubjectTypeRegistry(typeProvider);

        FluentStorageContainer? root = null;
        var services = new ServiceCollection();
        services.AddSingleton(typeProvider);
        services.AddSingleton(typeRegistry);
        services.AddSingleton<IInterceptorSubjectContext>(InterceptorSubjectContext.Create());
        services.AddSingleton<ConfigurableSubjectSerializer>();
        services.AddSingleton<MarkdownContentParser>();
        services.AddSingleton(new SubjectPathResolver(() => root));
        var serviceProvider = services.BuildServiceProvider();

        var storage = new FluentStorageContainer(
            typeRegistry, serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>(), serviceProvider, _logger)
        {
            ConnectionString = _directory.FullName,
            EnableFileWatching = enableFileWatching
        };
        root = storage;

        // Configuration refreshes find the configuration properties through the registry.
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);
        ((IInterceptorSubject)storage).Context.AddFallbackContext(context);

        return storage;
    }

    public void Dispose()
    {
        _directory.Delete(recursive: true);
        _otherDirectory.Delete(recursive: true);
    }
}
