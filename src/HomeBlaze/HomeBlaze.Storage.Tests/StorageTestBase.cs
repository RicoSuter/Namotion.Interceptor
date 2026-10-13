using System.Text;
using System.Text.Json;
using HomeBlaze.Services;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Storage.Tests;

/// <summary>
/// A storage container on a temporary directory, wired with the application's interceptor context.
/// </summary>
/// <remarks>
/// One collection for all derived classes: the gates and counters of <see cref="GatedFile"/> and the callback
/// of <see cref="CallbackSubject"/> are static, so tests that use them must not run next to each other.
/// Every other class that writes to an in-memory storage is in this collection as well: FluentStorage hashes
/// the content of such a write with one hash object that all its in-memory storages share, and that object
/// fails when two threads use it at once.
/// </remarks>
[Collection(nameof(StorageTestBase))]
public abstract class StorageTestBase : IDisposable
{
    protected static readonly TimeSpan WatcherTimeout = TimeSpan.FromSeconds(20);

    // Work that waits for something that never happens never completes, so the wait for it ends the test instead.
    protected static readonly TimeSpan DeadlockTimeout = TimeSpan.FromSeconds(30);

    private readonly List<FluentStorageContainer> _storages = [];
    private readonly List<DirectoryInfo> _temporaryDirectories = [];
    private ServiceProvider? _serviceProvider;

    protected StorageTestBase()
    {
        GatedFile.Reset();
        CallbackSubject.Reset();
    }

    protected DirectoryInfo StorageDirectory { get; } = Directory.CreateTempSubdirectory("homeblaze-storage-");

    /// <summary>The context of the storage connected last.</summary>
    protected IInterceptorSubjectContext? Context { get; private set; }

    protected async Task<FluentStorageContainer> ConnectAsync(
        bool enableFileWatching = false,
        Action<FluentStorageContainer>? configure = null)
    {
        var storage = CreateStorage(enableFileWatching, configure);
        await storage.ConnectAsync(CancellationToken.None);
        return storage;
    }

    /// <summary>
    /// The storage of <see cref="ConnectAsync"/> before it connects, for tests that start it another way.
    /// </summary>
    protected FluentStorageContainer CreateStorage(
        bool enableFileWatching = false,
        Action<FluentStorageContainer>? configure = null,
        ILogger<FluentStorageContainer>? logger = null)
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddAssembly(typeof(FluentStorageContainer).Assembly);
        typeProvider.AddAssembly(typeof(Samples.Motor).Assembly);
        typeProvider.AddAssembly(typeof(GatedFile).Assembly);
        var typeRegistry = new SubjectTypeRegistry(typeProvider);

        // The application's context, so that the Children setters run change tracking, registry and lifecycle code.
        var services = new ServiceCollection();
        var context = SubjectContextFactory.Create(services);
        services.AddSingleton(typeProvider);
        services.AddSingleton(typeRegistry);
        services.AddSingleton(context);
        services.AddSingleton<SubjectFactory>();
        services.AddSingleton<ConfigurableSubjectSerializer>();
        services.AddSingleton<RootManager>();
        services.AddSingleton(sp => new SubjectPathResolver(() => sp.GetRequiredService<RootManager>().Root));
        services.AddSingleton<MarkdownContentParser>();
        var serviceProvider = services.BuildServiceProvider();
        _serviceProvider = serviceProvider;
        Context = context;

        var storage = new FluentStorageContainer(
            typeRegistry,
            serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>(),
            serviceProvider,
            logger)
        {
            ConnectionString = StorageDirectory.FullName,
            EnableFileWatching = enableFileWatching
        };

        ((IInterceptorSubject)storage).Context.AddFallbackContext(context);
        configure?.Invoke(storage);
        _storages.Add(storage);
        return storage;
    }

    protected Samples.Motor CreateMotor(string name = "Motor")
        => new(_serviceProvider!.GetRequiredService<IInterceptorSubjectContext>()) { Name = name };

    protected static string SerializeMotor(string name)
    {
        // Serialized with services of its own, so the subject is not part of the storage under test.
        var typeProvider = new TypeProvider();
        typeProvider.AddAssembly(typeof(Samples.Motor).Assembly);

        var services = new ServiceCollection();
        services.AddSingleton(typeProvider);
        services.AddSingleton(new SubjectTypeRegistry(typeProvider));
        services.AddSingleton<IInterceptorSubjectContext>(InterceptorSubjectContext.Create());
        services.AddSingleton<SubjectFactory>();
        services.AddSingleton<ConfigurableSubjectSerializer>();

        using var serviceProvider = services.BuildServiceProvider();
        var motor = new Samples.Motor(serviceProvider.GetRequiredService<IInterceptorSubjectContext>()) { Name = name };
        return serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>().Serialize(motor);
    }

    /// <summary>The JSON file of a storage that is nested in another one and follows nothing by itself.</summary>
    protected static string SerializeStorage(string directory, string? containerName = null)
        => $$"""
            {
              "$type": "{{typeof(FluentStorageContainer).FullName}}",
              "storageType": "disk",
              "connectionString": {{JsonSerializer.Serialize(directory)}},
              "containerName": {{JsonSerializer.Serialize(containerName)}},
              "enableFileWatching": false,
              "reconcileIntervalSeconds": 0
            }
            """;

    protected string GetFullPath(string relativePath)
        => Path.Combine(StorageDirectory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));

    protected void WriteFile(string relativePath, string content = "content")
    {
        var fullPath = GetFullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    /// <summary>
    /// Replaces the content of a file with content of the same length and keeps its modification time, so that
    /// a listing shows no change.
    /// </summary>
    protected void ChangeContentOnly(string relativePath, string content)
    {
        var modified = File.GetLastWriteTimeUtc(GetFullPath(relativePath));
        WriteFile(relativePath, content);
        File.SetLastWriteTimeUtc(GetFullPath(relativePath), modified);
    }

    protected static async Task WriteBlobAsync(
        IStorageContainer storage, string path, string content, CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await storage.WriteBlobAsync(path, stream, cancellationToken);
    }

    /// <summary>A directory next to <see cref="StorageDirectory"/>, deleted with the test.</summary>
    protected DirectoryInfo CreateTemporaryDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("homeblaze-storage-");
        _temporaryDirectories.Add(directory);
        return directory;
    }

    protected static IReadOnlySet<string> Named(params string[] relativePaths)
        => relativePaths.ToHashSet(StringComparer.Ordinal);

    protected DetachCounter CountDetachesOf(IInterceptorSubject subject)
    {
        var counter = new DetachCounter(subject);
        Context!.AddService<ILifecycleHandler>(counter);
        return counter;
    }

    public void Dispose()
    {
        foreach (var storage in _storages)
        {
            storage.Dispose();
        }

        _serviceProvider?.Dispose();
        StorageDirectory.Delete(recursive: true);
        foreach (var directory in _temporaryDirectories)
        {
            directory.Delete(recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    protected sealed class DetachCounter(IInterceptorSubject subject) : ILifecycleHandler
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.IsContextDetach && ReferenceEquals(change.Subject, subject))
            {
                Interlocked.Increment(ref _count);
            }
        }
    }
}
