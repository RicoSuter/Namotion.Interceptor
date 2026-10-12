using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests;

public class FluentStorageContainerPathTests : IDisposable
{
    private readonly DirectoryInfo _dataDirectory = Directory.CreateTempSubdirectory("homeblaze-data-");
    private readonly DirectoryInfo _otherDirectory = Directory.CreateTempSubdirectory("homeblaze-other-");

    [Fact]
    public async Task WhenConnectionStringIsRelative_ThenStorageResolvesAgainstDataDirectory()
    {
        // Arrange
        var filesDirectory = Directory.CreateDirectory(Path.Combine(_dataDirectory.FullName, "Files"));
        File.WriteAllText(Path.Combine(filesDirectory.FullName, "notes.txt"), "hello");

        using var storage = CreateStorage(withDataDirectory: true);
        storage.ConnectionString = "Files";

        // Act
        await storage.ConnectAsync(CancellationToken.None);

        // Assert
        Assert.Equal(["notes.txt"], storage.Children.Keys);
    }

    [Fact]
    public async Task WhenConnectionStringIsAbsolute_ThenDataDirectoryIsIgnored()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_dataDirectory.FullName, "in-data-directory.txt"), "hello");
        File.WriteAllText(Path.Combine(_otherDirectory.FullName, "in-absolute-directory.txt"), "hello");

        using var storage = CreateStorage(withDataDirectory: true);
        storage.ConnectionString = _otherDirectory.FullName;

        // Act
        await storage.ConnectAsync(CancellationToken.None);

        // Assert
        Assert.Equal(["in-absolute-directory.txt"], storage.Children.Keys);
    }

    [Fact]
    public async Task WhenNoDataDirectoryIsProvided_ThenRelativeConnectionStringResolvesAgainstWorkingDirectory()
    {
        // Arrange
        var relativeDirectory = "homeblaze-relative-" + Guid.NewGuid().ToString("N");
        var workingDirectory = Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), relativeDirectory));
        try
        {
            File.WriteAllText(Path.Combine(workingDirectory.FullName, "notes.txt"), "hello");

            using var storage = CreateStorage(withDataDirectory: false);
            storage.ConnectionString = relativeDirectory;

            // Act
            await storage.ConnectAsync(CancellationToken.None);

            // Assert
            Assert.Equal(["notes.txt"], storage.Children.Keys);
        }
        finally
        {
            workingDirectory.Delete(recursive: true);
        }
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
            serviceProvider)
        {
            EnableFileWatching = false
        };

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
        _otherDirectory.Delete(recursive: true);
    }

    private sealed class TestDataDirectoryProvider(string dataDirectory) : IDataDirectoryProvider
    {
        public string DataDirectory => dataDirectory;
    }
}
