using HomeBlaze.Services;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Storage.Tests;

/// <summary>
/// A storage container on a temporary directory, wired with the application's interceptor context.
/// </summary>
/// <remarks>
/// One collection for all derived classes: the gates and counters of <see cref="GatedFile"/> are static, so
/// tests that use them must not run next to each other.
/// </remarks>
[Collection(nameof(StorageTestBase))]
public abstract class StorageTestBase : IDisposable
{
    protected static readonly TimeSpan WatcherTimeout = TimeSpan.FromSeconds(20);

    private readonly List<FluentStorageContainer> _storages = [];
    private readonly List<DirectoryInfo> _temporaryDirectories = [];
    private ServiceProvider? _serviceProvider;

    protected StorageTestBase()
    {
        GatedFile.Reset();
    }

    protected DirectoryInfo StorageDirectory { get; } = Directory.CreateTempSubdirectory("homeblaze-storage-");

    /// <summary>The context of the storage connected last.</summary>
    protected IInterceptorSubjectContext? Context { get; private set; }

    protected async Task<FluentStorageContainer> ConnectAsync(
        bool enableFileWatching = false,
        Action<FluentStorageContainer>? configure = null)
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
            serviceProvider)
        {
            ConnectionString = StorageDirectory.FullName,
            EnableFileWatching = enableFileWatching
        };

        ((IInterceptorSubject)storage).Context.AddFallbackContext(context);
        configure?.Invoke(storage);
        _storages.Add(storage);
        await storage.ConnectAsync(CancellationToken.None);
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

    protected string GetFullPath(string relativePath)
        => Path.Combine(StorageDirectory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));

    protected void WriteFile(string relativePath, string content = "content")
    {
        var fullPath = GetFullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
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
