using HomeBlaze.Services;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests;

public class FluentStorageContainerFileWatcherTests : IDisposable
{
    private readonly DirectoryInfo _storageDirectory = Directory.CreateTempSubdirectory("homeblaze-watcher-");

    [Fact]
    public async Task WhenConnectAsyncRunsTwice_ThenThePreviousFileWatcherIsDisposed()
    {
        // Arrange
        using var storage = CreateStorage();
        storage.ConnectionString = _storageDirectory.FullName;
        storage.EnableFileWatching = true;

        await storage.ConnectAsync(CancellationToken.None);
        var firstWatcher = storage.FileWatcher;

        // Act
        await storage.ConnectAsync(CancellationToken.None);
        var secondWatcher = storage.FileWatcher;

        // Assert
        Assert.NotNull(firstWatcher);
        Assert.NotNull(secondWatcher);
        Assert.NotSame(firstWatcher, secondWatcher);
        Assert.True(firstWatcher!.IsDisposed);
        Assert.False(secondWatcher!.IsDisposed);
    }

    [Fact]
    public async Task WhenConnectAsyncRunsTwiceWithFileWatchingDisabled_ThenThePreviousFileWatcherIsDisposed()
    {
        // Arrange
        using var storage = CreateStorage();
        storage.ConnectionString = _storageDirectory.FullName;
        storage.EnableFileWatching = true;

        await storage.ConnectAsync(CancellationToken.None);
        var firstWatcher = storage.FileWatcher;

        // Act
        storage.EnableFileWatching = false;
        await storage.ConnectAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(firstWatcher);
        Assert.True(firstWatcher!.IsDisposed);
        Assert.Null(storage.FileWatcher);
    }

    private FluentStorageContainer CreateStorage()
    {
        var typeProvider = new TypeProvider();
        var typeRegistry = new SubjectTypeRegistry(typeProvider);

        var services = new ServiceCollection();
        services.AddSingleton(typeProvider);
        services.AddSingleton(typeRegistry);
        services.AddSingleton<IInterceptorSubjectContext>(InterceptorSubjectContext.Create());
        services.AddSingleton<SubjectFactory>();
        services.AddSingleton<ConfigurableSubjectSerializer>();
        services.AddSingleton<RootManager>();
        services.AddSingleton(sp => new SubjectPathResolver(() => sp.GetRequiredService<RootManager>().Root));
        services.AddSingleton<MarkdownContentParser>();
        var serviceProvider = services.BuildServiceProvider();

        return new FluentStorageContainer(
            typeRegistry,
            serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>(),
            serviceProvider);
    }

    public void Dispose()
    {
        _storageDirectory.Delete(recursive: true);
    }
}
