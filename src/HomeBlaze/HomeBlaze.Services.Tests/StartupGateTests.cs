using HomeBlaze.Services.Tests.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Services.Tests;

public class StartupGateTests : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    private readonly List<IDisposable> _disposables = [];
    private readonly List<string> _configurationFiles = [];

    [Fact]
    public async Task WhenRootIsLoadedWithoutDeferrals_ThenGateCompletes()
    {
        // Arrange
        var gate = new StartupGate();
        var rootManager = CreateRootManager(CreateConfigurationFile(), gate);

        // Act
        await rootManager.StartAsync(CancellationToken.None);

        // Assert
        await gate.Completed.WaitAsync(WaitTimeout);
    }

    [Fact]
    public void WhenRootIsNotLoaded_ThenGateStaysOpen()
    {
        // Arrange
        var gate = new StartupGate();

        // Act
        gate.Defer().Dispose();

        // Assert
        Assert.False(gate.Completed.IsCompleted);
    }

    [Fact]
    public async Task WhenDeferralIsHeld_ThenGateCompletesOnlyOnceItIsReleased()
    {
        // Arrange
        var gate = new StartupGate();
        var deferral = gate.Defer();
        var rootManager = CreateRootManager(CreateConfigurationFile(), gate);
        await rootManager.StartAsync(CancellationToken.None);
        await rootManager.ExecuteTask!.WaitAsync(WaitTimeout);
        Assert.False(gate.Completed.IsCompleted);

        // Act
        deferral.Dispose();

        // Assert
        await gate.Completed.WaitAsync(WaitTimeout);
    }

    [Fact]
    public void WhenDeferralIsDisposedTwice_ThenItIsReleasedOnce()
    {
        // Arrange
        var gate = new StartupGate();
        var first = gate.Defer();
        var second = gate.Defer();
        gate.CompleteRootLoad();

        // Act
        first.Dispose();
        first.Dispose();

        // Assert
        Assert.False(gate.Completed.IsCompleted);
        second.Dispose();
        Assert.True(gate.Completed.IsCompletedSuccessfully);
    }

    [Fact]
    public void WhenGateHasCompleted_ThenLaterDeferralDoesNotReopenIt()
    {
        // Arrange
        var gate = new StartupGate();
        gate.CompleteRootLoad();
        Assert.True(gate.Completed.IsCompletedSuccessfully);

        // Act
        var deferral = gate.Defer();

        // Assert
        Assert.True(gate.Completed.IsCompletedSuccessfully);
        deferral.Dispose();
        Assert.True(gate.Completed.IsCompletedSuccessfully);
    }

    [Fact]
    public void WhenGateHasCompleted_ThenDeferringReturnsTheSameNoOpHandle()
    {
        // Arrange
        var gate = new StartupGate();
        gate.CompleteRootLoad();

        // Act
        var first = gate.Defer();
        var second = gate.Defer();

        // Assert
        Assert.Same(first, second);
    }

    [Fact]
    public async Task WhenDeferralIsReleasedWithATask_ThenItIsReleasedOnceTheTaskCompleted()
    {
        // Arrange
        var gate = new StartupGate();
        var task = new TaskCompletionSource();
        StartupGate.ReleaseWhenCompleted(gate.Defer(), task.Task);
        gate.CompleteRootLoad();
        Assert.False(gate.Completed.IsCompleted);

        // Act
        task.SetCanceled();

        // Assert
        await gate.Completed.WaitAsync(WaitTimeout);
    }

    [Fact]
    public async Task WhenReleaseDeferralDisposalFailsInTheContinuation_ThenTheFailureIsLoggedInsteadOfUnobserved()
    {
        // Arrange
        Exception? loggedException = null;
        var logger = new TestLogger(exception => Volatile.Write(ref loggedException, exception));
        var task = new TaskCompletionSource();

        // Act
        StartupGate.ReleaseWhenCompleted(new ThrowingDeferral(), task.Task, logger);
        task.SetResult();

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => Volatile.Read(ref loggedException) is not null, WaitTimeout);
        Assert.IsType<InvalidOperationException>(Volatile.Read(ref loggedException));
    }

    [Fact]
    public void WhenReleaseDeferralDisposalFailsInTheContinuationAndNoLoggerIsGiven_ThenTheFailureIsIgnored()
    {
        // Arrange
        var task = new TaskCompletionSource();

        // Act
        StartupGate.ReleaseWhenCompleted(new ThrowingDeferral(), task.Task);
        task.SetResult();

        // Assert: completing the task above does not throw or crash the test process.
    }

    [Fact]
    public async Task WhenRootFailsToLoad_ThenGateFaultsWithTheLoadException()
    {
        // Arrange
        var gate = new StartupGate();
        var missingFile = Path.Combine(Path.GetTempPath(), $"homeblaze-missing-root-{Guid.NewGuid():N}.json");
        var rootManager = CreateRootManager(missingFile, gate);

        // Act
        await rootManager.StartAsync(CancellationToken.None);

        // Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() => gate.Completed.WaitAsync(WaitTimeout));
    }

    [Fact]
    public async Task WhenHostedServiceStartIsPending_ThenGateCompletesOnlyOnceItHasStarted()
    {
        // Arrange
        using var probe = new ConfigurableStartupProbe();
        probe.Release.Set();
        var services = new ServiceCollection()
            .AddSingleton(probe)
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        var gate = new StartupGate();
        var context = InterceptorSubjectContext.Create().WithHostedServices(services);
        context.AddService(gate);
        services.AddSingleton(context);
        await using var serviceProvider = services.BuildServiceProvider();
        var handler = Assert.Single(serviceProvider.GetServices<IHostedService>());

        var path = CreateConfigurationFile($$"""
            {"$type":"{{typeof(ConfigurableStartupSubject).FullName}}","configuration":"configured"}
            """);
        var rootManager = CreateRootManager(path, context, serviceProvider, typeof(ConfigurableStartupSubject));
        await rootManager.StartAsync(CancellationToken.None);
        await rootManager.ExecuteTask!.WaitAsync(WaitTimeout);
        Assert.False(gate.Completed.IsCompleted);

        try
        {
            // Act
            await handler.StartAsync(CancellationToken.None);

            // Assert
            await probe.Started.Task.WaitAsync(WaitTimeout);
            await gate.Completed.WaitAsync(WaitTimeout);
        }
        finally
        {
            await handler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void WhenStartupCompletionIsDeferred_ThenEveryStartupCompletionIsDeferredUntilDisposed()
    {
        // Arrange
        var first = new CountingStartupCompletion();
        var second = new CountingStartupCompletion();
        var context = InterceptorSubjectContext.Create();
        context.AddService(first);
        context.AddService(second);

        // Act
        var deferral = context.DeferStartupCompletion();

        // Assert
        Assert.Equal(1, first.Deferrals);
        Assert.Equal(1, second.Deferrals);
        deferral.Dispose();
        deferral.Dispose();
        Assert.Equal(0, first.Deferrals);
        Assert.Equal(0, second.Deferrals);
    }

    [Fact]
    public void WhenNoStartupCompletionIsRegistered_ThenDeferringIsANoOp()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();

        // Act
        var deferral = context.DeferStartupCompletion();

        // Assert
        deferral.Dispose();
        Assert.True(context.IsStartupCompleted());
        Assert.True(context.WaitForStartupAsync(CancellationToken.None).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WhenGateIsOpen_ThenWaitingForStartupCompletesOnceItCompletes()
    {
        // Arrange
        var gate = new StartupGate();
        var context = InterceptorSubjectContext.Create();
        context.AddService(gate);

        // Act
        var waitTask = context.WaitForStartupAsync(CancellationToken.None);

        // Assert
        Assert.False(waitTask.IsCompleted);
        Assert.False(context.IsStartupCompleted());
        gate.CompleteRootLoad();
        await waitTask.WaitAsync(WaitTimeout);
        Assert.True(context.IsStartupCompleted());
    }

    [Fact]
    public async Task WhenWaitingForStartupIsCancelled_ThenTheWaitIsCancelled()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.AddService(new StartupGate());
        using var cancellation = new CancellationTokenSource();
        var waitTask = context.WaitForStartupAsync(cancellation.Token);

        // Act
        await cancellation.CancelAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitTask);
    }

    private RootManager CreateRootManager(string configurationFilePath, StartupGate gate)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        context.AddService(gate);

        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        _disposables.Add(serviceProvider);
        return CreateRootManager(configurationFilePath, context, serviceProvider, typeof(TestSubject));
    }

    private RootManager CreateRootManager(
        string configurationFilePath, IInterceptorSubjectContext context, IServiceProvider serviceProvider, Type rootType)
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([rootType]);

        var typeRegistry = new SubjectTypeRegistry(typeProvider);
        var serializer = new ConfigurableSubjectSerializer(typeProvider, serviceProvider);

        var configuration = new Mock<IConfiguration>();
        configuration
            .Setup(instance => instance["HomeBlaze:RootConfigFile"])
            .Returns(configurationFilePath);

        RootManager? rootManager = null;
        var pathResolver = new SubjectPathResolver(() => rootManager!.Root);
        rootManager = new RootManager(typeRegistry, serializer, context, pathResolver, configuration.Object);

        _disposables.Add(rootManager);
        return rootManager;
    }

    private string CreateConfigurationFile(string? content = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"homeblaze-root-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content ?? $$"""
            {
              "$type": "{{typeof(TestSubject).FullName}}",
              "configProperty": "loaded"
            }
            """);

        _configurationFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        foreach (var configurationFile in _configurationFiles)
        {
            File.Delete(configurationFile);
        }
    }

    private sealed class ThrowingDeferral : IDisposable
    {
        public void Dispose() => throw new InvalidOperationException("Release failed.");
    }

    private sealed class TestLogger(Action<Exception> onException) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                onException(exception);
            }
        }
    }

    private sealed class CountingStartupCompletion : IStartupCompletion
    {
        private int _deferrals;

        public int Deferrals => Volatile.Read(ref _deferrals);

        public IDisposable Defer()
        {
            Interlocked.Increment(ref _deferrals);
            return new Release(this);
        }

        private sealed class Release(CountingStartupCompletion owner) : IDisposable
        {
            public void Dispose() => Interlocked.Decrement(ref owner._deferrals);
        }
    }
}
