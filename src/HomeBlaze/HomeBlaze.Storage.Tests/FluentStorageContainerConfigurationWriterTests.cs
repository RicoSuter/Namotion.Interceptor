using FluentStorage.Blobs;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Tests;

public class FluentStorageContainerConfigurationWriterTests : StorageTestBase
{
    private const string FilePath = "layout.json";

    [Fact]
    public async Task WhenSubjectNestedInFileConfigurationIsWritten_ThenItsFileIsWritten()
    {
        // Arrange
        var storage = await ConnectAsync();
        var nested = new ConfigurationWriterTestSubject(Context!);
        var intermediate = new ConfigurationWriterTestSubject(Context!) { Items = [nested] };
        var file = new ConfigurationWriterTestSubject(Context!) { Items = [intermediate] };
        await storage.AddSubjectAsync(FilePath, file, CancellationToken.None);
        nested.Name = "changed";

        // Act
        var isWritten = await storage.WriteConfigurationAsync(nested, CancellationToken.None);

        // Assert
        Assert.True(isWritten);
        Assert.Contains("\"name\": \"changed\"", await ReadFileAsync());
    }

    [Fact]
    public async Task WhenSubjectHeldThroughStatePropertyIsWritten_ThenNothingIsWritten()
    {
        // Arrange
        var storage = await ConnectAsync();
        var held = new ConfigurationWriterTestSubject(Context!);
        var file = new ConfigurationWriterTestSubject(Context!) { StateItem = held };
        await storage.AddSubjectAsync(FilePath, file, CancellationToken.None);
        var originalContent = await ReadFileAsync();
        file.Name = "changed";

        // Act
        var isWritten = await storage.WriteConfigurationAsync(held, CancellationToken.None);

        // Assert
        Assert.False(isWritten);
        Assert.Equal(originalContent, await ReadFileAsync());
    }

    [Fact]
    public async Task WhenTwoSubjectsOfOneFileAreSavedAtOnce_ThenTheFileHoldsBothChanges()
    {
        // Arrange
        PausableBlobStorage? client = null;
        var storage = await ConnectAsync(configure: container => container.ClientDecorator = inner => client = new PausableBlobStorage(inner));
        var first = new ConfigurationWriterTestSubject(Context!);
        var second = new ConfigurationWriterTestSubject(Context!);
        var file = new ConfigurationWriterTestSubject(Context!) { Items = [first, second] };
        await storage.AddSubjectAsync(FilePath, file, CancellationToken.None);
        first.Name = "first, saved";
        var firstWriteReached = client!.PauseNext(nameof(IBlobStorage.WriteAsync));
        var firstSave = storage.WriteConfigurationAsync(first, CancellationToken.None);
        await firstWriteReached;

        // Act: the second save is asked for while the first one writes.
        second.Name = "second, saved";
        var secondSave = storage.WriteConfigurationAsync(second, CancellationToken.None);
        client.Release();
        var results = await Task.WhenAll(firstSave, secondSave).WaitAsync(DeadlockTimeout);

        // Assert
        Assert.Equal([true, true], results);
        var content = await ReadFileAsync();
        Assert.Contains("\"name\": \"first, saved\"", content);
        Assert.Contains("\"name\": \"second, saved\"", content);
    }

    private Task<string> ReadFileAsync() => File.ReadAllTextAsync(GetFullPath(FilePath));
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

    public ConfigurationWriterTestSubject()
    {
        Name = string.Empty;
        Items = [];
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
