using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.OpcUa.Tests;

public class OpcUaServerStartupTests : IAsyncLifetime
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    private readonly string _configurationPath = Path.Combine(Path.GetTempPath(), $"homeblaze-opcua-root-{Guid.NewGuid():N}.json");
    private readonly StartupGate _gate = new();
    private ServiceProvider? _serviceProvider;
    private RootManager? _rootManager;
    private SubjectPathResolver? _pathResolver;

    public async Task InitializeAsync()
    {
        await File.WriteAllTextAsync(_configurationPath, $$"""{ "$type": "{{typeof(TestRoot).FullName}}" }""");

        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(TestRoot)]);
        _serviceProvider = new ServiceCollection().BuildServiceProvider();

        var configuration = new Mock<IConfiguration>();
        configuration.Setup(instance => instance["HomeBlaze:RootConfigFile"]).Returns(_configurationPath);

        _pathResolver = new SubjectPathResolver(() => _rootManager!.Root);
        _rootManager = new RootManager(
            new SubjectTypeRegistry(typeProvider),
            new ConfigurableSubjectSerializer(typeProvider, _serviceProvider),
            InterceptorSubjectContext.Create(),
            _pathResolver,
            configuration.Object);

        await _rootManager.StartAsync(CancellationToken.None);
        await _rootManager.RootLoaded.WaitAsync(WaitTimeout);
    }

    [Fact]
    public async Task WhenStartupIsPending_ThenStartWaitsAndReportsIt()
    {
        // Arrange
        var server = CreateServer();

        // Act
        var startTask = server.StartAsync();

        // Assert
        Assert.False(startTask.IsCompleted);
        Assert.Equal(ServiceStatus.Starting, server.Status);
        Assert.Equal("Waiting for startup to complete", server.StatusMessage);

        _gate.CompleteRootLoad();
        await startTask.WaitAsync(WaitTimeout);
        Assert.Equal(ServiceStatus.Error, server.Status);
        Assert.StartsWith("Could not resolve subject", server.StatusMessage);
    }

    [Fact]
    public async Task WhenStoppedWhileWaitingForStartup_ThenTheServerDoesNotStart()
    {
        // Arrange
        var server = CreateServer();
        var startTask = server.StartAsync();

        // Act
        await server.StopAsync();

        // Assert
        await startTask.WaitAsync(WaitTimeout);
        Assert.False(server.IsEnabled);
        Assert.Equal(ServiceStatus.Stopped, server.Status);
        Assert.Null(server.StatusMessage);
    }

    [Fact]
    public async Task WhenStartupDoesNotCompleteInTime_ThenTheServerStartsAnyway()
    {
        // Arrange
        var server = CreateServer();
        server.StartupWaitTimeout = TimeSpan.FromMilliseconds(50);

        // Act
        await server.StartAsync().WaitAsync(WaitTimeout);

        // Assert
        Assert.False(_gate.Completed.IsCompleted);
        Assert.Equal(ServiceStatus.Error, server.Status);
        Assert.StartsWith("Could not resolve subject", server.StatusMessage);
    }

    [Fact]
    public async Task WhenConfigurationIsAppliedBeforeStartupCompleted_ThenItDoesNotWaitAndStartsLater()
    {
        // Arrange
        var server = CreateServer();

        // Act
        await server.ApplyConfigurationAsync(CancellationToken.None).WaitAsync(WaitTimeout);

        // Assert
        Assert.False(_gate.Completed.IsCompleted);
        _gate.CompleteRootLoad();
        await AsyncTestHelpers.WaitUntilAsync(() => server.Status == ServiceStatus.Error);
        Assert.StartsWith("Could not resolve subject", server.StatusMessage);
    }

    private OpcUaServer CreateServer()
    {
        var server = new OpcUaServer(_rootManager!, _pathResolver!, NullLogger<OpcUaServer>.Instance)
        {
            Name = "Test",
            Path = "/Missing"
        };

        var context = InterceptorSubjectContext.Create();
        context.AddService(_gate);
        ((IInterceptorSubject)server).Context.AddFallbackContext(context);
        return server;
    }

    public Task DisposeAsync()
    {
        _rootManager?.Dispose();
        _serviceProvider?.Dispose();
        File.Delete(_configurationPath);
        return Task.CompletedTask;
    }
}

[InterceptorSubject]
public partial class TestRoot : IConfigurable
{
    [Configuration]
    public partial string? Name { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
