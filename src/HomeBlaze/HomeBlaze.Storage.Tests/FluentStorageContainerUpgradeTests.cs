using HomeBlaze.Samples;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.Storage.Tests;

public class FluentStorageContainerUpgradeTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("homeblaze-upgrade-");

    [Fact]
    public async Task WhenTypeIsAddedLater_ThenUnknownSubjectIsUpgradedAtSamePath()
    {
        // Arrange
        WriteFile("Devices/Motor1.json", """{ "$type": "HomeBlaze.Samples.Motor" }""");
        var (storage, typeProvider) = CreateStorage();
        using var _ = storage;
        await storage.ConnectAsync(CancellationToken.None);
        var folder = Assert.IsType<VirtualFolder>(storage.Children["Devices"]);
        Assert.IsType<UnknownSubject>(folder.Children["Motor1"]);

        // Act
        typeProvider.AddAssembly(typeof(Motor).Assembly);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() =>
            ((VirtualFolder)storage.Children["Devices"]).Children.TryGetValue("Motor1", out var subject) && subject is Motor);
    }

    [Fact]
    public async Task WhenUnknownSubjectIsRewrittenWithKnownType_ThenItIsRecreated()
    {
        // Arrange
        WriteFile("Motor1.json", """{ "$type": "HomeBlaze.Samples.Motorr" }""");
        var (storage, typeProvider) = CreateStorage();
        using var _ = storage;
        typeProvider.AddAssembly(typeof(Motor).Assembly);
        await storage.ConnectAsync(CancellationToken.None);
        var unknown = Assert.IsType<UnknownSubject>(storage.Children["Motor1"]);

        // Act
        await unknown.WriteAsync(new MemoryStream("""{ "$type": "HomeBlaze.Samples.Motor" }"""u8.ToArray()), CancellationToken.None);

        // Assert
        Assert.IsType<Motor>(storage.Children["Motor1"]);
    }

    [Fact]
    public async Task WhenInvalidJsonPlaceholderIsRewrittenWithValidJson_ThenItIsRecreated()
    {
        // Arrange
        WriteFile("Motor1.json", """{ "$type": "HomeBlaze.Samples.Motor", """);
        var (storage, typeProvider) = CreateStorage();
        using var _ = storage;
        typeProvider.AddAssembly(typeof(Motor).Assembly);
        await storage.ConnectAsync(CancellationToken.None);
        var unknown = Assert.IsType<UnknownSubject>(storage.Children["Motor1"]);
        Assert.Equal(string.Empty, unknown.TypeName);

        // Act
        await unknown.WriteAsync(new MemoryStream("""{ "$type": "HomeBlaze.Samples.Motor" }"""u8.ToArray()), CancellationToken.None);

        // Assert
        Assert.IsType<Motor>(storage.Children["Motor1"]);
    }

    [Fact]
    public async Task WhenUnknownSubjectFileChangesOnDisk_ThenItIsRecreated()
    {
        // Arrange
        WriteFile("Motor1.json", """{ "$type": "HomeBlaze.Samples.Motorr" }""");
        var (storage, typeProvider) = CreateStorage(enableFileWatching: true);
        using var _ = storage;
        typeProvider.AddAssembly(typeof(Motor).Assembly);
        await storage.ConnectAsync(CancellationToken.None);
        Assert.IsType<UnknownSubject>(storage.Children["Motor1"]);

        // The simulated event is the only one: a real one raised concurrently would race it into the event stream.
        storage.FileWatcher!.Watcher!.EnableRaisingEvents = false;

        // Act
        WriteFile("Motor1.json", """{ "$type": "HomeBlaze.Samples.Motor" }""");
        storage.FileWatcher.SimulateFileEvent(
            new FileSystemEventArgs(WatcherChangeTypes.Changed, _directory.FullName, "Motor1.json"));

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() =>
            storage.Children.TryGetValue("Motor1", out var subject) && subject is Motor);
    }

    [Fact]
    public async Task WhenTypesChange_ThenPlainJsonFilesAndUnresolvedPlaceholdersAreKept()
    {
        // Arrange
        WriteFile("Data.json", """{ "name": "plain data" }""");
        WriteFile("Sensor1.json", """{ "$type": "MyCompany.Sensor" }""");
        var (storage, typeProvider) = CreateStorage();
        using var _ = storage;
        await storage.ConnectAsync(CancellationToken.None);
        var jsonFile = storage.Children["Data.json"];
        var unknown = storage.Children["Sensor1"];

        // Act
        typeProvider.AddAssembly(typeof(Motor).Assembly);
        await storage.UpgradeUnknownSubjectsAsync();

        // Assert
        Assert.Same(jsonFile, storage.Children["Data.json"]);
        Assert.Same(unknown, storage.Children["Sensor1"]);
    }

    private void WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_directory.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private (FluentStorageContainer Storage, TypeProvider TypeProvider) CreateStorage(bool enableFileWatching = false)
    {
        var typeProvider = new TypeProvider();
        var typeRegistry = new SubjectTypeRegistry(typeProvider);

        var services = new ServiceCollection();
        services.AddSingleton(typeProvider);
        services.AddSingleton(typeRegistry);
        services.AddSingleton<IInterceptorSubjectContext>(InterceptorSubjectContext.Create());
        services.AddSingleton<ConfigurableSubjectSerializer>();
        services.AddSingleton<MarkdownContentParser>();
        var serviceProvider = services.BuildServiceProvider();

        var storage = new FluentStorageContainer(typeRegistry, serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>(), serviceProvider)
        {
            ConnectionString = _directory.FullName,
            EnableFileWatching = enableFileWatching
        };

        return (storage, typeProvider);
    }

    public void Dispose()
    {
        _directory.Delete(recursive: true);
    }
}
