using System.Collections.Concurrent;
using System.Text.Json;
using HomeBlaze.Samples;
using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    private readonly CountingLifecycleHandler _lifecycle = new();

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
    public async Task WhenRescanningAgainAfterTypeChange_ThenTheRecreatedSubjectIsKept()
    {
        // Arrange
        WriteFile("Device.json", CounterJson("first"));
        using var storage = CreateStorage();
        await storage.ConnectAsync(CancellationToken.None);
        WriteFile("Device.json", """{ "$type": "HomeBlaze.Samples.Motor", "name": "Motor" }""");
        await storage.ApplyConfigurationAsync(CancellationToken.None);
        var motor = Assert.IsType<Motor>(storage.Children["Device"]);

        // Act
        await storage.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Same(motor, storage.Children["Device"]);
    }

    [Fact]
    public async Task WhenSubjectsAreKeptRemovedAndRecreated_ThenOnlyChangedSubjectsAreAttachedOrDetached()
    {
        // Arrange
        WriteFile("kept.txt", "kept");
        WriteFile("removed.txt", "removed");
        WriteFile("Device.json", CounterJson("first"));
        using var storage = CreateStorage();
        await storage.ConnectAsync(CancellationToken.None);
        var kept = storage.Children["kept.txt"];
        var removed = storage.Children["removed.txt"];
        var counter = storage.Children["Device"];

        // Act
        File.Delete(Path.Combine(_directory.FullName, "removed.txt"));
        WriteFile("Device.json", """{ "$type": "HomeBlaze.Samples.Motor" }""");
        WriteFile("added.txt", "added");
        await storage.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        var motor = Assert.IsType<Motor>(storage.Children["Device"]);
        var added = storage.Children["added.txt"];
        Assert.Equal((1, 0), _lifecycle.GetCounts(kept));
        Assert.Equal((1, 1), _lifecycle.GetCounts(removed));
        Assert.Equal((1, 1), _lifecycle.GetCounts(counter));
        Assert.Equal((1, 0), _lifecycle.GetCounts(motor));
        Assert.Equal((1, 0), _lifecycle.GetCounts(added));
    }

    [Fact]
    public async Task WhenNestedStorageFileIsUnchanged_ThenNestedStorageIsKeptWithoutReconnecting()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_otherDirectory.FullName, "inner.txt"), "inner");
        WriteFile("Nested.json", NestedStorageJson(containerName: null));
        using var storage = CreateStorage();
        await storage.ConnectAsync(CancellationToken.None);
        var nested = Assert.IsType<FluentStorageContainer>(storage.Children["Nested"]);
        await nested.ConnectAsync(CancellationToken.None);
        var nestedChildren = nested.Children;

        // Act
        await storage.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Same(nested, storage.Children["Nested"]);
        Assert.Same(nestedChildren, nested.Children);
        Assert.Equal(1, CountNestedConnects());
    }

    [Fact]
    public async Task WhenNestedStorageFileChanged_ThenNestedStorageIsReconfiguredAndReconcilesItsChildren()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_otherDirectory.FullName, "inner.txt"), "inner");
        WriteFile("Nested.json", NestedStorageJson(containerName: null));
        using var storage = CreateStorage();
        await storage.ConnectAsync(CancellationToken.None);
        var nested = Assert.IsType<FluentStorageContainer>(storage.Children["Nested"]);
        await nested.ConnectAsync(CancellationToken.None);
        var innerFile = nested.Children["inner.txt"];

        // Act
        WriteFile("Nested.json", NestedStorageJson(containerName: "changed"));
        await storage.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Same(nested, storage.Children["Nested"]);
        Assert.Equal("changed", nested.ContainerName);
        Assert.Equal(2, CountNestedConnects());
        Assert.Same(innerFile, nested.Children["inner.txt"]);
    }

    [Fact]
    public async Task WhenOnlyModificationTimeChangedAtSameSize_ThenDocumentsAreRefreshed()
    {
        // Arrange
        WriteFile("Readme.md", "# First");
        WriteFile("notes.txt", "notes");
        using var storage = CreateStorage();
        await storage.ConnectAsync(CancellationToken.None);
        var markdownFile = Assert.IsType<MarkdownFile>(storage.Children["Readme.md"]);
        var genericFile = Assert.IsType<GenericFile>(storage.Children["notes.txt"]);
        var modified = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        // Act
        WriteFile("Readme.md", "# Fyrst");
        File.SetLastWriteTimeUtc(Path.Combine(_directory.FullName, "Readme.md"), modified);
        File.SetLastWriteTimeUtc(Path.Combine(_directory.FullName, "notes.txt"), modified);
        await storage.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Same(markdownFile, storage.Children["Readme.md"]);
        Assert.Equal("# Fyrst", markdownFile.Content);
        Assert.Same(genericFile, storage.Children["notes.txt"]);
        Assert.Equal(modified, genericFile.LastModified);
    }

    [Fact]
    public async Task WhenPlaceholderFileStillGivesPlaceholder_ThenInstanceIsKeptWithTheNewReason()
    {
        // Arrange
        WriteFile("Sensor1.json", """{ "$type": "MyCompany.Sensor" }""");
        using var storage = CreateStorage();
        await storage.ConnectAsync(CancellationToken.None);
        var placeholder = Assert.IsType<UnknownSubject>(storage.Children["Sensor1"]);
        Assert.Equal(UnknownSubject.TypeNotLoadedReason, placeholder.Reason);

        // Act
        WriteFile("Sensor1.json", $$"""{ "$type": "{{typeof(NonConfigurableSubject).FullName}}" }""");
        await storage.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Same(placeholder, storage.Children["Sensor1"]);
        Assert.Equal(typeof(NonConfigurableSubject).FullName, placeholder.TypeName);
        Assert.Equal(UnknownSubject.TypeNotConfigurableReason, placeholder.Reason);
    }

    [Fact]
    public async Task WhenFileNamesDifferOnlyInCase_ThenRescansKeepBothSubjectsAndTheRegistryStable()
    {
        // Arrange
        WriteFile("Foo.txt", "upper");
        WriteFile("foo.txt", "lower");
        if (Directory.GetFiles(_directory.FullName).Length < 2)
        {
            // A case-insensitive file system holds one file, so there is nothing to keep apart.
            return;
        }

        using var storage = CreateStorage();
        await storage.ConnectAsync(CancellationToken.None);
        var upper = storage.Children["Foo.txt"];
        var lower = storage.Children["foo.txt"];

        // Act
        await storage.ApplyConfigurationAsync(CancellationToken.None);
        await storage.ApplyConfigurationAsync(CancellationToken.None);
        File.Delete(Path.Combine(_directory.FullName, "foo.txt"));
        await storage.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.NotSame(upper, lower);
        Assert.Equal(3, _logger.CountMessages("Found 2 subjects"));
        Assert.Equal(1, _logger.CountMessages("Found 1 subjects"));
        Assert.Equal(["Foo.txt"], storage.Children.Keys);
        Assert.Same(upper, storage.Children["Foo.txt"]);
        await storage.DeleteSubjectAsync(upper, CancellationToken.None);
        Assert.Empty(storage.Children);
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

    private int CountNestedConnects() => _logger.CountMessages($"Connected to storage: disk at {_otherDirectory.FullName}");

    private string NestedStorageJson(string? containerName) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["$type"] = typeof(FluentStorageContainer).FullName,
        ["storageType"] = "disk",
        ["connectionString"] = _otherDirectory.FullName,
        ["containerName"] = containerName,
        ["enableFileWatching"] = false
    });

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
        typeProvider.AddTypes([typeof(CountingConfigurableSubject), typeof(NonConfigurableSubject)]);
        var typeRegistry = new SubjectTypeRegistry(typeProvider);

        FluentStorageContainer? root = null;
        var services = new ServiceCollection();
        services.AddSingleton(typeProvider);
        services.AddSingleton(typeRegistry);
        services.AddSingleton<IInterceptorSubjectContext>(InterceptorSubjectContext.Create());
        services.AddSingleton<ConfigurableSubjectSerializer>();
        services.AddSingleton<MarkdownContentParser>();
        services.AddSingleton(new SubjectPathResolver(() => root));
        services.AddSingleton<ILogger<FluentStorageContainer>>(_logger);
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
                handler => handler is PropertyAttributeInitializer)
            .WithService<ILifecycleHandler>(() => _lifecycle, handler => handler == _lifecycle);
        ((IInterceptorSubject)storage).Context.AddFallbackContext(context);

        return storage;
    }

    private sealed class CountingLifecycleHandler : ILifecycleHandler
    {
        private readonly ConcurrentDictionary<IInterceptorSubject, (int Attaches, int Detaches)> _counts = new();

        public (int Attaches, int Detaches) GetCounts(IInterceptorSubject subject)
            => _counts.GetValueOrDefault(subject);

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.IsContextAttach)
            {
                _counts.AddOrUpdate(change.Subject, (1, 0), (_, counts) => (counts.Attaches + 1, counts.Detaches));
            }

            if (change.IsContextDetach)
            {
                _counts.AddOrUpdate(change.Subject, (0, 1), (_, counts) => (counts.Attaches, counts.Detaches + 1));
            }
        }
    }

    public void Dispose()
    {
        _directory.Delete(recursive: true);
        _otherDirectory.Delete(recursive: true);
    }
}
