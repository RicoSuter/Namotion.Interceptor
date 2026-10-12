using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Tests;

public class FluentStorageContainerConfigurationWriterTests : IAsyncLifetime
{
    private const string FilePath = "layout.json";

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("homeblaze-writer-");
    private readonly ServiceProvider _serviceProvider;
    private readonly IInterceptorSubjectContext _context;
    private readonly FluentStorageContainer _storage;

    public FluentStorageContainerConfigurationWriterTests()
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(ConfigurationWriterTestSubject)]);
        var typeRegistry = new SubjectTypeRegistry(typeProvider);

        // The context of the application: the writer finds the file of a nested subject through the parents
        // and the configuration properties of its registry entry.
        var services = new ServiceCollection();
        _context = SubjectContextFactory.Create(services);

        services.AddSingleton(typeProvider);
        services.AddSingleton(typeRegistry);
        services.AddSingleton(_context);
        services.AddSingleton<SubjectFactory>();
        services.AddSingleton<ConfigurableSubjectSerializer>();
        services.AddSingleton<RootManager>();
        services.AddSingleton(sp => new SubjectPathResolver(() => sp.GetRequiredService<RootManager>().Root));
        services.AddSingleton<MarkdownContentParser>();
        _serviceProvider = services.BuildServiceProvider();

        _storage = new FluentStorageContainer(
            typeRegistry,
            _serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>(),
            _serviceProvider)
        {
            ConnectionString = _directory.FullName,
            EnableFileWatching = false
        };
    }

    public Task InitializeAsync() => _storage.ConnectAsync(CancellationToken.None);

    [Fact]
    public async Task WhenSubjectNestedInFileConfigurationIsWritten_ThenItsFileIsWritten()
    {
        // Arrange
        var nested = new ConfigurationWriterTestSubject(_context);
        var intermediate = new ConfigurationWriterTestSubject(_context) { Items = [nested] };
        var file = new ConfigurationWriterTestSubject(_context) { Items = [intermediate] };
        await _storage.AddSubjectAsync(FilePath, file, CancellationToken.None);
        nested.Name = "changed";

        // Act
        var isWritten = await _storage.WriteConfigurationAsync(nested, CancellationToken.None);

        // Assert
        Assert.True(isWritten);
        Assert.Contains("\"name\": \"changed\"", await ReadFileAsync());
    }

    [Fact]
    public async Task WhenSubjectHeldThroughStatePropertyIsWritten_ThenNothingIsWritten()
    {
        // Arrange
        var held = new ConfigurationWriterTestSubject(_context);
        var file = new ConfigurationWriterTestSubject(_context) { StateItem = held };
        await _storage.AddSubjectAsync(FilePath, file, CancellationToken.None);
        var originalContent = await ReadFileAsync();
        file.Name = "changed";

        // Act
        var isWritten = await _storage.WriteConfigurationAsync(held, CancellationToken.None);

        // Assert
        Assert.False(isWritten);
        Assert.Equal(originalContent, await ReadFileAsync());
    }

    [Fact]
    public async Task WhenSubjectIsEditedAndWrittenWhileItsFileIsSerialized_ThenTheFileHoldsTheEdit()
    {
        // Arrange
        var first = new ConfigurationWriterTestSubject(_context);
        var second = new ConfigurationWriterTestSubject(_context);
        var file = new ConfigurationWriterTestSubject(_context) { Items = [first, second] };
        await _storage.AddSubjectAsync(FilePath, file, CancellationToken.None);

        // The items are serialized in order, so the write that reaches the second item has read the first one.
        // The later write is started from there, which is in the middle of the first write.
        var isLaterWriteStarted = false;
        Task<bool>? laterWrite = null;
        second.Serializing = () =>
        {
            if (!isLaterWriteStarted)
            {
                isLaterWriteStarted = true;
                first.Name = "edited";
                laterWrite = _storage.WriteConfigurationAsync(first, CancellationToken.None);
            }
        };

        // Act
        var isWritten = await _storage.WriteConfigurationAsync(second, CancellationToken.None);
        var isLaterWritten = await laterWrite!;

        // Assert
        Assert.True(isWritten);
        Assert.True(isLaterWritten);
        Assert.Contains("\"name\": \"edited\"", await ReadFileAsync());
    }

    private Task<string> ReadFileAsync()
        => File.ReadAllTextAsync(Path.Combine(_directory.FullName, FilePath));

    public async Task DisposeAsync()
    {
        _storage.Dispose();
        await _serviceProvider.DisposeAsync();
        _directory.Delete(recursive: true);
    }
}

[InterceptorSubject]
public partial class ConfigurationWriterTestSubject : IConfigurable
{
    [Configuration]
    public partial string Name { get; set; }

    [Configuration]
    public partial List<ConfigurationWriterTestSubject> Items { get; set; }

    [State]
    public partial ConfigurationWriterTestSubject? StateItem { get; set; }

    /// <summary>
    /// Called each time the subject is serialized, through the property below that the serializer reads.
    /// </summary>
    public Action? Serializing { get; set; }

    [Configuration]
    public string? SerializationProbe
    {
        get
        {
            Serializing?.Invoke();
            return null;
        }
        set => _ = value;
    }

    public ConfigurationWriterTestSubject()
    {
        Name = string.Empty;
        Items = [];
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
