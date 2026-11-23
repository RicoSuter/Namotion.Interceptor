using HomeBlaze.Services;
using HomeBlaze.Services.Secrets;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace HomeBlaze.Storage.Tests;

public class FileSubjectFactoryTests
{
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
        var directory = Directory.CreateTempSubdirectory("homeblaze-secrets-");
        try
        {
            var factory = CreateFactoryWithSecretStore(Path.Combine(directory.FullName, "Secrets.json"));
            var json = $$"""{ "$type": "{{typeof(EmbeddedSecretDevice).FullName}}", "host": "10.0.0.1", "password": {{password}} }""";

            // Act
            factory.CreateFromJson(Mock.Of<IStorageContainer>(), "/Gateway.json", json, out var hasUnstoredSecrets);

            // Assert
            Assert.Equal(expected, hasUnstoredSecrets);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void WhenAJsonFileWithAPlaintextSecretHasAValueThatCannotBeRead_ThenItIsNotReportedForARewrite()
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("homeblaze-secrets-");
        try
        {
            var factory = CreateFactoryWithSecretStore(Path.Combine(directory.FullName, "Secrets.json"));
            var json = $$"""{ "$type": "{{typeof(EmbeddedSecretDevice).FullName}}", "host": [1], "password": "s3cr3t" }""";

            // Act
            factory.CreateFromJson(Mock.Of<IStorageContainer>(), "/Gateway.json", json, out var hasUnstoredSecrets);

            // Assert
            Assert.False(hasUnstoredSecrets);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void WhenAJsonFileWithAPlaintextSecretHasAValueThatCannotBeRead_ThenAWarningTellsThatTheSecretStaysInTheFile()
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("homeblaze-secrets-");
        try
        {
            var logger = new LevelRecordingLogger();
            var factory = CreateFactoryWithSecretStore(Path.Combine(directory.FullName, "Secrets.json"), logger);
            var json = $$"""{ "$type": "{{typeof(EmbeddedSecretDevice).FullName}}", "host": [1], "password": "s3cr3t" }""";

            // Act
            factory.CreateFromJson(Mock.Of<IStorageContainer>(), "/Gateway.json", json, out _);

            // Assert
            Assert.Contains(logger.Messages, message => message.Contains("/Gateway.json") && message.Contains("plaintext"));
            Assert.DoesNotContain(logger.Messages, message => message.Contains("s3cr3t"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void WhenCheckingAJsonFileForPlaintextSecretsFails_ThenTheSubjectIsCreatedAndAWarningIsLogged()
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("homeblaze-secrets-");
        try
        {
            var logger = new LevelRecordingLogger();
            var factory = CreateFactoryWithSecretStore(Path.Combine(directory.FullName, "Secrets.json"), logger);
            var json = $$"""{ "$type": "{{typeof(UnserializableSecretDevice).FullName}}", "value": {}, "password": "s3cr3t" }""";

            // Act
            var subject = factory.CreateFromJson(Mock.Of<IStorageContainer>(), "/Gateway.json", json, out var hasUnstoredSecrets);

            // Assert
            Assert.IsType<UnserializableSecretDevice>(subject);
            Assert.False(hasUnstoredSecrets);
            Assert.Contains(logger.Messages, message => message.Contains("/Gateway.json") && message.Contains("plaintext"));
            Assert.DoesNotContain(logger.Messages, message => message.Contains("s3cr3t"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static FileSubjectFactory CreateFactoryWithSecretStore(string secretsPath, ILogger? logger = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new SecretStore(secretsPath));
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
