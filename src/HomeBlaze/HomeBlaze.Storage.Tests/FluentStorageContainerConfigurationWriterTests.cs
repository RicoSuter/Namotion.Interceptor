using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Storage.Tests;

public class FluentStorageContainerConfigurationWriterTests : IDisposable
{
    private const string FilePath = "layout.json";

    private readonly IInterceptorSubjectContext _context = InterceptorSubjectContext
        .Create()
        .WithFullPropertyTracking()
        .WithRegistry()
        .WithParents()
        .WithLifecycle()
        .WithService<IPropertyLifecycleHandler>(
            () => new PropertyAttributeInitializer(),
            handler => handler is PropertyAttributeInitializer);

    private readonly List<IDisposable> _disposables = [];

    [Fact]
    public async Task WhenFileSubjectIsWritten_ThenItsFileIsWritten()
    {
        // Arrange
        var storage = await ConnectAsync();
        var file = new ConfigurationWriterTestFile(_context);
        await storage.AddSubjectAsync(FilePath, file, CancellationToken.None);
        file.Name = "changed";

        // Act
        var isWritten = await storage.WriteConfigurationAsync(file, CancellationToken.None);

        // Assert
        Assert.True(isWritten);
        Assert.Contains("\"name\": \"changed\"", await ReadFileAsync(storage));
    }

    [Fact]
    public async Task WhenSubjectNestedInFileConfigurationIsWritten_ThenItsFileIsWritten()
    {
        // Arrange
        var storage = await ConnectAsync();
        var nested = new ConfigurationWriterTestFile(_context);
        var intermediate = new ConfigurationWriterTestFile(_context) { Items = [nested] };
        var file = new ConfigurationWriterTestFile(_context) { Items = [intermediate] };
        await storage.AddSubjectAsync(FilePath, file, CancellationToken.None);
        nested.Name = "changed";

        // Act
        var isWritten = await storage.WriteConfigurationAsync(nested, CancellationToken.None);

        // Assert
        Assert.True(isWritten);
        Assert.Contains("\"name\": \"changed\"", await ReadFileAsync(storage));
    }

    [Fact]
    public async Task WhenSubjectHeldThroughStatePropertyIsWritten_ThenNothingIsWritten()
    {
        // Arrange
        var storage = await ConnectAsync();
        var held = new ConfigurationWriterTestFile(_context);
        var file = new ConfigurationWriterTestFile(_context) { StateItem = held };
        await storage.AddSubjectAsync(FilePath, file, CancellationToken.None);
        var originalContent = await ReadFileAsync(storage);
        file.Name = "changed";

        // Act
        var isWritten = await storage.WriteConfigurationAsync(held, CancellationToken.None);

        // Assert
        Assert.False(isWritten);
        Assert.Equal(originalContent, await ReadFileAsync(storage));
    }

    private async Task<FluentStorageContainer> ConnectAsync()
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(ConfigurationWriterTestFile)]);
        var typeRegistry = new SubjectTypeRegistry(typeProvider);

        var services = new ServiceCollection();
        services.AddSingleton(typeProvider);
        services.AddSingleton(typeRegistry);
        services.AddSingleton(_context);
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
            StorageType = "inmemory"
        };

        _disposables.Add(storage);
        _disposables.Add(serviceProvider);

        await storage.ConnectAsync(CancellationToken.None);
        return storage;
    }

    private static async Task<string> ReadFileAsync(FluentStorageContainer storage)
    {
        await using var stream = await storage.ReadBlobAsync(FilePath, CancellationToken.None);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }
}

[InterceptorSubject]
public partial class ConfigurationWriterTestFile : IConfigurable
{
    [Configuration]
    public partial string Name { get; set; }

    [Configuration]
    public partial List<ConfigurationWriterTestFile> Items { get; set; }

    [State]
    public partial ConfigurationWriterTestFile? StateItem { get; set; }

    public ConfigurationWriterTestFile()
    {
        Name = string.Empty;
        Items = [];
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
