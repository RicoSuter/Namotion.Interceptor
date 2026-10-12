using HomeBlaze.Services;
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
        var subject = factory.CreateFromJson(Mock.Of<IStorageContainer>(), "/Values.json", json);

        // Assert
        Assert.IsType<JsonFile>(subject);
        Assert.DoesNotContain(logger.Levels, level => level >= LogLevel.Warning);
    }

    private sealed class LevelRecordingLogger : ILogger
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Levels.Add(logLevel);
    }
}
