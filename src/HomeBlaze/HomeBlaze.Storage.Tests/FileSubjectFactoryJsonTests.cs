using HomeBlaze.Samples;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests;

public class FileSubjectFactoryJsonTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("homeblaze-json-");

    [Fact]
    public async Task WhenTypeResolves_ThenSubjectIsCreated()
    {
        // Arrange
        WriteFile("Motor1.json", """{ "$type": "HomeBlaze.Samples.Motor" }""");
        using var storage = CreateStorage(typeof(Motor).Assembly.GetExportedTypes());

        // Act
        await storage.ConnectAsync(CancellationToken.None);

        // Assert
        Assert.IsType<Motor>(storage.Children["Motor1"]);
    }

    [Fact]
    public async Task WhenTypeDoesNotResolve_ThenUnknownSubjectIsCreatedUnderKeyWithoutExtension()
    {
        // Arrange
        WriteFile("Sensor1.json", """{ "$type": "MyCompany.Sensor", "name": "Kitchen" }""");
        using var storage = CreateStorage([]);

        // Act
        await storage.ConnectAsync(CancellationToken.None);

        // Assert
        var unknown = Assert.IsType<UnknownSubject>(storage.Children["Sensor1"]);
        Assert.Equal("MyCompany.Sensor", unknown.TypeName);
        Assert.Equal(UnknownSubject.TypeNotLoadedReason, unknown.Reason);
    }

    [Fact]
    public async Task WhenTypeIsLoadedButNotConfigurable_ThenUnknownSubjectExplainsIt()
    {
        // Arrange
        WriteFile("Plain.json", $$"""{ "$type": "{{typeof(NonConfigurableSubject).FullName}}" }""");
        using var storage = CreateStorage([typeof(NonConfigurableSubject)]);

        // Act
        await storage.ConnectAsync(CancellationToken.None);

        // Assert
        var unknown = Assert.IsType<UnknownSubject>(storage.Children["Plain"]);
        Assert.Equal(typeof(NonConfigurableSubject).FullName, unknown.TypeName);
        Assert.Equal(UnknownSubject.TypeNotConfigurableReason, unknown.Reason);
    }

    [Fact]
    public async Task WhenConstructionThrows_ThenUnknownSubjectHasErrorAsReason()
    {
        // Arrange
        WriteFile("Broken.json", $$"""{ "$type": "{{typeof(ThrowingSubject).FullName}}" }""");
        using var storage = CreateStorage([typeof(ThrowingSubject)]);

        // Act
        await storage.ConnectAsync(CancellationToken.None);

        // Assert
        var unknown = Assert.IsType<UnknownSubject>(storage.Children["Broken"]);
        Assert.Contains("Device driver is broken.", unknown.Reason);
    }

    [Fact]
    public async Task WhenConstructionThrowsWithCause_ThenUnknownSubjectReasonKeepsOuterMessage()
    {
        // Arrange
        WriteFile("Broken2.json", $$"""{ "$type": "{{typeof(ThrowingSubjectWithCause).FullName}}" }""");
        using var storage = CreateStorage([typeof(ThrowingSubjectWithCause)]);

        // Act
        await storage.ConnectAsync(CancellationToken.None);

        // Assert
        var unknown = Assert.IsType<UnknownSubject>(storage.Children["Broken2"]);
        Assert.Contains("Could not open port", unknown.Reason);
    }

    [Theory]
    [InlineData("""{ "name": "plain data" }""")]
    [InlineData("""[1, 2, 3]""")]
    [InlineData("""{ not json""")]
    public async Task WhenJsonHasNoType_ThenJsonFileIsCreated(string content)
    {
        // Arrange
        WriteFile("Data.json", content);
        using var storage = CreateStorage([]);

        // Act
        await storage.ConnectAsync(CancellationToken.None);

        // Assert
        Assert.IsType<JsonFile>(storage.Children["Data.json"]);
    }

    [Theory]
    [InlineData("""{ "$type": 123 }""")]
    [InlineData("""{ "$type": "" }""")]
    [InlineData("""{ "$type": "   " }""")]
    public async Task WhenTypeValueIsNotAUsableString_ThenJsonFileIsCreated(string content)
    {
        // Arrange
        WriteFile("Data.json", content);
        using var storage = CreateStorage([]);

        // Act
        await storage.ConnectAsync(CancellationToken.None);

        // Assert
        Assert.IsType<JsonFile>(storage.Children["Data.json"]);
    }

    [Fact]
    public async Task WhenJsonIsInvalidButContainsTypeMarker_ThenUnknownSubjectIsCreatedUnderKeyWithoutExtension()
    {
        // Arrange
        WriteFile("Truncated.json", """{ "$type": "HomeBlaze.Samples.Motor", """);
        using var storage = CreateStorage([]);

        // Act
        await storage.ConnectAsync(CancellationToken.None);

        // Assert
        var unknown = Assert.IsType<UnknownSubject>(storage.Children["Truncated"]);
        Assert.Equal(string.Empty, unknown.TypeName);
        Assert.StartsWith("Invalid JSON", unknown.Reason);
    }

    [Fact]
    public async Task WhenDeletingPlaceholder_ThenItIsRemovedFromChildren()
    {
        // Arrange
        WriteFile("Sensor1.json", """{ "$type": "MyCompany.Sensor" }""");
        using var storage = CreateStorage([]);
        await storage.ConnectAsync(CancellationToken.None);
        var unknown = Assert.IsType<UnknownSubject>(storage.Children["Sensor1"]);

        // Act
        await storage.DeleteSubjectAsync(unknown, CancellationToken.None);

        // Assert
        Assert.False(storage.Children.ContainsKey("Sensor1"));
    }

    private void WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_directory.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private FluentStorageContainer CreateStorage(IEnumerable<Type> types)
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes(types);
        var typeRegistry = new SubjectTypeRegistry(typeProvider);

        var services = new ServiceCollection();
        services.AddSingleton(typeProvider);
        services.AddSingleton(typeRegistry);
        services.AddSingleton<IInterceptorSubjectContext>(InterceptorSubjectContext.Create());
        services.AddSingleton<ConfigurableSubjectSerializer>();
        services.AddSingleton<MarkdownContentParser>();
        var serviceProvider = services.BuildServiceProvider();

        return new FluentStorageContainer(typeRegistry, serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>(), serviceProvider)
        {
            ConnectionString = _directory.FullName,
            EnableFileWatching = false
        };
    }

    public void Dispose()
    {
        _directory.Delete(recursive: true);
    }
}
