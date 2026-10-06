using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using HomeBlaze.Samples;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

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

        // Act
        WriteFile("Motor1.json", """{ "$type": "HomeBlaze.Samples.Motor" }""");
        storage.FileWatcher!.SimulateFileEvent(
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

    [Fact]
    public async Task WhenStalePlaceholderInstanceIsWrittenAfterUpgrade_ThenLiveSubjectIsRefreshed()
    {
        // Arrange
        WriteFile("Motor1.json", """{ "$type": "HomeBlaze.Samples.Motorr" }""");
        var (storage, typeProvider) = CreateStorage();
        using var _ = storage;

        // Configuration refreshes find the configuration properties through the registry.
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);
        ((IInterceptorSubject)storage).Context.AddFallbackContext(context);
        typeProvider.AddAssembly(typeof(Motor).Assembly);
        await storage.ConnectAsync(CancellationToken.None);
        var placeholder = Assert.IsType<UnknownSubject>(storage.Children["Motor1"]);
        await placeholder.WriteAsync(ToStream("""{ "$type": "HomeBlaze.Samples.Motor", "name": "First" }"""), CancellationToken.None);
        var motor = Assert.IsType<Motor>(storage.Children["Motor1"]);

        // Act
        await placeholder.WriteAsync(ToStream("""{ "$type": "HomeBlaze.Samples.Motor", "name": "Second" }"""), CancellationToken.None);

        // Assert
        Assert.Same(motor, storage.Children["Motor1"]);
        Assert.Equal("Second", motor.Name);
    }

    [Fact]
    public async Task WhenPlaceholderKeyCollidesWithFolder_ThenUpgradeKeepsTheFolder()
    {
        // Arrange
        WriteFile("Motor1/readme.txt", "folder content");
        WriteFile("Motor1.json", """{ "$type": "HomeBlaze.Samples.Motor" }""");
        var (storage, typeProvider) = CreateStorage();
        using var _ = storage;
        await storage.ConnectAsync(CancellationToken.None);
        var folder = Assert.IsType<VirtualFolder>(storage.Children["Motor1"]);

        // Act
        typeProvider.AddAssembly(typeof(Motor).Assembly);
        await storage.UpgradeUnknownSubjectsAsync();

        // Assert
        Assert.Same(folder, storage.Children["Motor1"]);
        Assert.True(folder.Children.ContainsKey("readme.txt"));
    }

    [Fact]
    public async Task WhenPlaceholderIsRewrittenWithAnotherUnknownType_ThenInstanceIsKeptAndUpdated()
    {
        // Arrange
        WriteFile("Sensor1.json", """{ "$type": "MyCompany.Sensor" }""");
        var (storage, _) = CreateStorage();
        using var __ = storage;
        await storage.ConnectAsync(CancellationToken.None);
        var placeholder = Assert.IsType<UnknownSubject>(storage.Children["Sensor1"]);

        // Act
        await placeholder.WriteAsync(ToStream("""{ "$type": "MyCompany.OtherSensor" }"""), CancellationToken.None);

        // Assert
        Assert.Same(placeholder, storage.Children["Sensor1"]);
        Assert.Equal("MyCompany.OtherSensor", placeholder.TypeName);
    }

    [Fact]
    public async Task WhenStorageIsStopped_ThenTypesChangedDoesNotUpgradeItsPlaceholders()
    {
        // Arrange
        WriteFile("Motor1.json", """{ "$type": "HomeBlaze.Samples.Motor" }""");
        var (storage, typeProvider) = CreateStorage();
        using var _ = storage;
        await storage.ConnectAsync(CancellationToken.None);
        var placeholder = Assert.IsType<UnknownSubject>(storage.Children["Motor1"]);

        // Act
        await storage.StopAsync(CancellationToken.None);
        typeProvider.AddAssembly(typeof(Motor).Assembly);

        // Assert
        Assert.Equal(0, GetTypesChangedHandlerCount(typeProvider));
        Assert.Same(placeholder, storage.Children["Motor1"]);
    }

    [Fact]
    public async Task WhenStorageReconnects_ThenItSubscribesToTypesChangedOnce()
    {
        // Arrange
        var (storage, typeProvider) = CreateStorage();
        using var _ = storage;

        // Act
        await storage.ConnectAsync(CancellationToken.None);
        await storage.ConnectAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, GetTypesChangedHandlerCount(typeProvider));
    }

    [Fact]
    public async Task WhenStorageIsDisposed_ThenItUnsubscribesFromTypesChanged()
    {
        // Arrange
        var (storage, typeProvider) = CreateStorage();
        await storage.ConnectAsync(CancellationToken.None);

        // Act
        storage.Dispose();

        // Assert
        Assert.Equal(0, GetTypesChangedHandlerCount(typeProvider));
    }

    [Fact]
    public async Task WhenTypesChange_ThenInvalidJsonPlaceholderIsNotReread()
    {
        // Arrange
        WriteFile("Truncated.json", """{ "$type": "HomeBlaze.Samples.Motor", """);
        var logger = new CapturingLogger();
        var (storage, typeProvider) = CreateStorage(logger: logger);
        using var _ = storage;
        await storage.ConnectAsync(CancellationToken.None);
        var placeholder = Assert.IsType<UnknownSubject>(storage.Children["Truncated"]);
        var warningCount = logger.CountMessages("Invalid JSON");

        // Act
        typeProvider.AddAssembly(typeof(Motor).Assembly);
        await storage.UpgradeUnknownSubjectsAsync();

        // Assert
        Assert.Equal(1, warningCount);
        Assert.Equal(warningCount, logger.CountMessages("Invalid JSON"));
        Assert.Same(placeholder, storage.Children["Truncated"]);
    }

    private static MemoryStream ToStream(string content) => new(Encoding.UTF8.GetBytes(content));

    private static int GetTypesChangedHandlerCount(TypeProvider typeProvider)
    {
        var handler = (Delegate?)typeof(TypeProvider)
            .GetField(nameof(TypeProvider.TypesChanged), BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(typeProvider);

        return handler?.GetInvocationList().Length ?? 0;
    }

    private void WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_directory.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private (FluentStorageContainer Storage, TypeProvider TypeProvider) CreateStorage(
        bool enableFileWatching = false, ILogger<FluentStorageContainer>? logger = null)
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

        var storage = new FluentStorageContainer(typeRegistry, serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>(), serviceProvider, logger)
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

    private sealed class CapturingLogger : ILogger<FluentStorageContainer>
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public int CountMessages(string fragment) => _messages.Count(message => message.Contains(fragment, StringComparison.Ordinal));

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _messages.Enqueue(formatter(state, exception));
    }
}
