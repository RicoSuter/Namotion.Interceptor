using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests;

public class FluentStorageContainerPathTests : IDisposable
{
    private readonly DirectoryInfo _dataDirectory = Directory.CreateTempSubdirectory("homeblaze-data-");

    [Fact]
    public async Task WhenConnectionStringIsRelative_ThenStorageResolvesAgainstDataDirectory()
    {
        // Arrange
        var filesDirectory = Directory.CreateDirectory(Path.Combine(_dataDirectory.FullName, "Files"));
        File.WriteAllText(Path.Combine(filesDirectory.FullName, "notes.txt"), "hello");

        using var storage = CreateStorage(withDataDirectory: true);
        storage.ConnectionString = "Files";
        storage.EnableFileWatching = false;

        // Act
        await storage.ConnectAsync(CancellationToken.None);

        // Assert
        Assert.Single(storage.Children);
        Assert.Equal(Path.Combine(filesDirectory.FullName, "notes.txt"), storage.GetFileSystemPath("notes.txt"));
    }

    [Fact]
    public void WhenConnectionStringIsAbsolute_ThenDataDirectoryIsIgnored()
    {
        // Arrange
        var absoluteDirectory = Path.Combine(Path.GetTempPath(), "homeblaze-absolute");
        var storage = CreateStorage(withDataDirectory: true);
        storage.ConnectionString = absoluteDirectory;

        // Act
        var path = storage.GetFileSystemPath("/Devices/a.json");

        // Assert
        Assert.Equal(Path.Combine(absoluteDirectory, "Devices", "a.json"), path);
    }

    [Fact]
    public void WhenNoDataDirectoryIsProvided_ThenRelativeConnectionStringResolvesAgainstWorkingDirectory()
    {
        // Arrange
        var storage = CreateStorage(withDataDirectory: false);
        storage.ConnectionString = "Files";

        // Act
        var path = storage.GetFileSystemPath("a.json");

        // Assert
        Assert.Equal(Path.GetFullPath(Path.Combine("Files", "a.json")), path);
    }

    private FluentStorageContainer CreateStorage(bool withDataDirectory)
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

        var storage = new FluentStorageContainer(
            typeRegistry,
            serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>(),
            serviceProvider);

        if (withDataDirectory)
        {
            var context = InterceptorSubjectContext.Create();
            context.AddService<IDataDirectoryProvider>(new TestDataDirectoryProvider(_dataDirectory.FullName));
            ((IInterceptorSubject)storage).Context.AddFallbackContext(context);
        }

        return storage;
    }

    public void Dispose()
    {
        _dataDirectory.Delete(recursive: true);
    }

    private sealed class TestDataDirectoryProvider(string dataDirectory) : IDataDirectoryProvider
    {
        public string DataDirectory => dataDirectory;
    }
}
