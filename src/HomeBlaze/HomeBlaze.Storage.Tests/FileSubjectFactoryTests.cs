using HomeBlaze.Services;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Namotion.Interceptor.Tracking.Lifecycle;

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

    [Fact]
    public async Task WhenAFileWithAValueThatCannotBeReadCreatesAHostedSubject_ThenItsWorkerStartsWithTheIssues()
    {
        // Arrange
        var typeProvider = new TypeProvider();
        typeProvider.AddAssembly(typeof(IssueReceivingWorker).Assembly);
        var services = new ServiceCollection();
        var context = SubjectContextFactory.Create(services);
        services.AddSingleton(typeProvider);
        services.AddSingleton(context);
        services.AddSingleton<ConfigurableSubjectSerializer>();
        using var serviceProvider = services.BuildServiceProvider();
        var factory = new FileSubjectFactory(
            new SubjectTypeRegistry(typeProvider), serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>(), serviceProvider);
        var host = context.GetServices<ILifecycleHandler>().OfType<IHostedService>().Single();
        await host.StartAsync(CancellationToken.None);
        var json = $$"""{ "$type": "{{typeof(IssueReceivingWorker).FullName}}", "count": "not a number" }""";

        // Act
        var workers = new List<IssueReceivingWorker>();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            workers.Add(Assert.IsType<IssueReceivingWorker>(factory.CreateFromJson(Mock.Of<IStorageContainer>(), $"/Worker{attempt}.json", json)));
        }

        var issuesAtStart = await Task.WhenAll(workers.Select(worker => worker.IssuesAtStart)).WaitAsync(TimeSpan.FromSeconds(30));
        await host.StopAsync(CancellationToken.None);

        // Assert
        Assert.All(issuesAtStart, issues => Assert.Single(issues));
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
