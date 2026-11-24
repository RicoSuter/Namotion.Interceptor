using HomeBlaze.Services;
using HomeBlaze.Services.Secrets;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace HomeBlaze.Storage.Tests;

public class FileSubjectFactoryTests : IDisposable
{
    private readonly DirectoryInfo _secretsDirectory = Directory.CreateTempSubdirectory("homeblaze-secrets-");

    [Theory]
    [InlineData("[1, 2, 3]")]
    [InlineData("42")]
    [InlineData("""{ "name": "no type" }""")]
    [InlineData("{")]
    [InlineData("")]
    public void WhenJsonDescribesNoSubject_ThenJsonFileIsCreatedAndNothingIsLoggedAsWarningOrError(string json)
    {
        // Arrange
        var logger = new LevelRecordingLogger();
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var typeProvider = new TypeProvider();
        var factory = new FileSubjectFactory(
            new SubjectTypeRegistry(typeProvider),
            new ConfigurableSubjectSerializer(typeProvider, serviceProvider),
            serviceProvider,
            logger);

        // Act
        var subject = factory.CreateFromJson(Mock.Of<IStorageContainer>(), "/Values.json", json, out _);

        // Assert
        Assert.IsType<JsonFile>(subject);
        Assert.DoesNotContain(logger.Levels, level => level >= LogLevel.Warning);
    }

    [Fact]
    public void WhenAValueOfTheFileCannotBeRead_ThenTheSubjectIsCreatedAndAWarningIsLogged()
    {
        // Arrange
        var logger = new LevelRecordingLogger();
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(CallbackSubject)]);
        var factory = new FileSubjectFactory(
            new SubjectTypeRegistry(typeProvider),
            new ConfigurableSubjectSerializer(typeProvider, serviceProvider),
            serviceProvider,
            logger);
        var json = $$"""{ "$type": "{{typeof(CallbackSubject).FullName}}", "name": [1] }""";

        // Act
        var subject = factory.CreateFromJson(Mock.Of<IStorageContainer>(), "/Callback.json", json, out _);

        // Assert
        Assert.IsType<CallbackSubject>(subject);
        Assert.Contains(LogLevel.Warning, logger.Levels);
    }

    [Theory]
    [InlineData("\"s3cr3t\"", true)]
    [InlineData("null", false)]
    public void WhenAJsonFileIsCreated_ThenItTellsWhetherItHoldsAPlaintextSecret(string password, bool expected)
    {
        // Arrange
        var factory = CreateFactoryWithSecretStore();
        var json = $$"""{ "$type": "{{typeof(EmbeddedSecretDevice).FullName}}", "host": "10.0.0.1", "password": {{password}} }""";

        // Act
        factory.CreateFromJson(Mock.Of<IStorageContainer>(), "/Gateway.json", json, out var shouldMoveSecrets);

        // Assert
        Assert.Equal(expected, shouldMoveSecrets);
    }

    [Fact]
    public void WhenAJsonFileWithAPlaintextSecretHasAValueThatCannotBeRead_ThenItIsNotReportedAndAWarningTellsThatTheSecretStaysInTheFile()
    {
        // Arrange
        var logger = new LevelRecordingLogger();
        var factory = CreateFactoryWithSecretStore(logger);
        var json = $$"""{ "$type": "{{typeof(EmbeddedSecretDevice).FullName}}", "host": [1], "password": "s3cr3t" }""";

        // Act
        factory.CreateFromJson(Mock.Of<IStorageContainer>(), "/Gateway.json", json, out var shouldMoveSecrets);

        // Assert
        Assert.False(shouldMoveSecrets);
        Assert.Contains(logger.Messages, message => message.Contains("/Gateway.json") && message.Contains("plaintext"));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("s3cr3t"));
    }

    [Fact]
    public void WhenCheckingAJsonFileForPlaintextSecretsFails_ThenTheSubjectIsCreatedAndAWarningIsLogged()
    {
        // Arrange
        var logger = new LevelRecordingLogger();
        var factory = CreateFactoryWithSecretStore(logger);
        var json = $$"""{ "$type": "{{typeof(UnserializableSecretDevice).FullName}}", "value": {}, "password": "s3cr3t" }""";

        // Act
        var subject = factory.CreateFromJson(Mock.Of<IStorageContainer>(), "/Gateway.json", json, out var shouldMoveSecrets);

        // Assert
        Assert.IsType<UnserializableSecretDevice>(subject);
        Assert.False(shouldMoveSecrets);
        Assert.Contains(logger.Messages, message => message.Contains("/Gateway.json") && message.Contains("plaintext"));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("s3cr3t"));
    }

    [Fact]
    public void WhenASubjectFromJsonIsReleased_ThenAnotherSubjectThatHoldsItsSecretKeyKeepsItOnChange()
    {
        // Arrange
        var store = new SecretStore(Path.Combine(_secretsDirectory.FullName, "Secrets.json"));
        var key = Guid.NewGuid().ToString("N");
        store.Set(key, "shared");
        var services = new ServiceCollection();
        services.AddSingleton(SubjectContextFactory.Create(services));
        services.AddSingleton(store);
        var serviceProvider = services.BuildServiceProvider();
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(EmbeddedSecretDevice)]);
        var serializer = new ConfigurableSubjectSerializer(typeProvider, serviceProvider);
        var factory = new FileSubjectFactory(new SubjectTypeRegistry(typeProvider), serializer, serviceProvider, logger: null);
        var json = $$"""{ "$type": "{{typeof(EmbeddedSecretDevice).FullName}}", "password": "$secret:{{key}}" }""";
        var released = factory.CreateFromJson(Mock.Of<IStorageContainer>(), "/Gateway.json", json, out _);
        var kept = Assert.IsType<EmbeddedSecretDevice>(serializer.Deserialize(json));
        kept.Password = "changed";

        // Act
        factory.Release(released);
        var keptJson = serializer.Serialize(kept);

        // Assert
        Assert.Contains($"$secret:{key}", keptJson);
        Assert.True(store.TryGet(key, out var value));
        Assert.Equal("changed", value);
        GC.KeepAlive(released);
    }

    public void Dispose() => _secretsDirectory.Delete(recursive: true);

    private FileSubjectFactory CreateFactoryWithSecretStore(ILogger? logger = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new SecretStore(Path.Combine(_secretsDirectory.FullName, "Secrets.json")));
        var serviceProvider = services.BuildServiceProvider();
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(EmbeddedSecretDevice), typeof(UnserializableSecretDevice)]);
        return new FileSubjectFactory(
            new SubjectTypeRegistry(typeProvider),
            new ConfigurableSubjectSerializer(typeProvider, serviceProvider),
            serviceProvider,
            logger);
    }

    private sealed class LevelRecordingLogger : ILogger
    {
        public List<LogLevel> Levels { get; } = [];

        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Levels.Add(logLevel);
            Messages.Add(formatter(state, exception));
        }
    }
}
