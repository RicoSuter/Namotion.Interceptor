using HomeBlaze.Services.Diagnostics;
using HomeBlaze.Services.Lifecycle;
using HomeBlaze.Storage.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace HomeBlaze.Services.Tests.Diagnostics;

public class HostLogsTests
{
    [Fact]
    public void WhenHostSystemIsCreated_ThenLogsHasDefaultConfiguration()
    {
        // Arrange
        var store = new RecentLogStore();

        // Act
        var host = CreateHost(store);

        // Assert
        Assert.Equal("Logs", host.Logs.Title);
        Assert.Null(host.Logs.MinimumLevel);
        Assert.Equal(1000, host.Logs.Capacity);
        Assert.Equal(0, host.Logs.TotalWarnings);
    }

    [Fact]
    public void WhenCreatedAfterEntriesWereKept_ThenCountersStartFromTheStore()
    {
        // Arrange
        var store = new RecentLogStore();
        store.Add("Category", LogLevel.Error, default, "Error", null);

        // Act
        var logs = new HostLogs(store);

        // Assert
        Assert.Equal(1, logs.TotalErrors);
        Assert.Equal(1, logs.EntryCount);
    }

    [Fact]
    public void WhenUpdated_ThenCountersReportTheStore()
    {
        // Arrange
        var store = new RecentLogStore();
        var logs = CreateHost(store).Logs;
        store.Add("Category", LogLevel.Warning, default, "Warning", null);
        store.Add("Category", LogLevel.Warning, default, "Warning", null);
        store.Add("Category", LogLevel.Error, default, "Error", null);

        // Act
        logs.Update();

        // Assert
        Assert.Equal(2, logs.TotalWarnings);
        Assert.Equal(1, logs.TotalErrors);
        Assert.Equal(3, logs.EntryCount);
    }

    [Fact]
    public async Task WhenLogsConfigurationIsApplied_ThenStoreFollowsIt()
    {
        // Arrange
        var store = new RecentLogStore();
        var logs = CreateHost(store).Logs;
        logs.MinimumLevel = LogLevel.Information;
        logs.Capacity = 5;

        // Act
        await logs.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(LogLevel.Information, store.MinimumLevel);
        Assert.Equal(5, store.Capacity);
    }

    [Fact]
    public async Task WhenHostSystemConfigurationIsApplied_ThenLogsConfigurationIsApplied()
    {
        // Arrange
        var store = new RecentLogStore();
        var host = CreateHost(store);
        host.Logs = new HostLogs(store) { MinimumLevel = LogLevel.Error, Capacity = 3 };

        // Act
        await host.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(LogLevel.Error, store.MinimumLevel);
        Assert.Equal(3, store.Capacity);
    }

    [Fact]
    public void WhenRecentLogsAreQueriedWithoutMaxCount_ThenTheNewest50AreReturnedNewestFirst()
    {
        // Arrange
        var store = new RecentLogStore();
        var logs = CreateHost(store).Logs;
        for (var index = 1; index <= 150; index++)
        {
            store.Add("Category", LogLevel.Warning, default, "Entry " + index, null);
        }

        // Act
        var entries = logs.GetRecentLogs();

        // Assert
        Assert.Equal(50, entries.Count);
        Assert.Equal("Entry 150", entries[0].Message);
        Assert.Equal("Entry 101", entries[^1].Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public void WhenMaxCountIsOutOfRange_ThenQueryThrows(int maxCount)
    {
        // Arrange
        var logs = CreateHost(new RecentLogStore()).Logs;

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => logs.GetRecentLogs(maxCount: maxCount));
    }

    [Theory]
    [InlineData(LogLevel.None)]
    [InlineData((LogLevel)42)]
    public async Task WhenLogLevelIsChangedToNoneOrNoLevel_ThenItThrowsAndNothingChanges(LogLevel level)
    {
        // Arrange
        var store = new RecentLogStore();
        var logs = CreateHost(store).Logs;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => logs.ChangeLogLevelAsync(level, CancellationToken.None));
        Assert.Null(logs.MinimumLevel);
        Assert.Equal(LogLevel.Warning, store.MinimumLevel);
    }

    [Fact]
    public async Task WhenLogLevelIsChanged_ThenItIsAppliedAndSaved()
    {
        // Arrange
        var store = new RecentLogStore();
        var host = CreateHost(store);
        var writer = new TestHostConfigurationWriter(CreateRegistryContext()) { IsWriting = true, Host = host };

        // Act
        await host.Logs.ChangeLogLevelAsync(LogLevel.Debug, CancellationToken.None);

        // Assert
        Assert.Equal(LogLevel.Debug, host.Logs.MinimumLevel);
        Assert.Equal(LogLevel.Debug, store.MinimumLevel);
        Assert.Same(host.Logs, Assert.Single(writer.WrittenSubjects));
    }

    [Fact]
    public async Task WhenLogLevelIsChangedToNull_ThenTheDefaultIsRestored()
    {
        // Arrange
        var store = new RecentLogStore();
        var logs = CreateHost(store).Logs;
        await logs.ChangeLogLevelAsync(LogLevel.Error, CancellationToken.None);

        // Act
        await logs.ChangeLogLevelAsync(null, CancellationToken.None);

        // Assert
        Assert.Null(logs.MinimumLevel);
        Assert.Equal(LogLevel.Warning, store.MinimumLevel);
    }

    [Fact]
    public async Task WhenLogLevelIsChangedButNotSaved_ThenWarningIsLogged()
    {
        // Arrange
        var store = new RecentLogStore();
        using var provider = new RecentLogLoggerProvider(store);
        var host = new HostSystem(null, () => { }, provider.CreateLogger("HostSystem"), store);
        var writer = new TestHostConfigurationWriter(CreateRegistryContext()) { IsWriting = false, Host = host };

        // Act
        await host.Logs.ChangeLogLevelAsync(LogLevel.Debug, CancellationToken.None);

        // Assert
        Assert.Single(writer.WrittenSubjects);
        Assert.Single(store.GetEntries(LogLevel.Warning, null, "not saved", null, 10));
    }

    [Fact]
    public async Task WhenLogLevelIsChangedWithoutConfigurationWriter_ThenWarningIsLogged()
    {
        // Arrange
        var store = new RecentLogStore();
        using var provider = new RecentLogLoggerProvider(store);
        var host = new HostSystem(null, () => { }, provider.CreateLogger("HostSystem"), store);

        // Act
        await host.Logs.ChangeLogLevelAsync(LogLevel.Information, CancellationToken.None);

        // Assert
        Assert.Single(store.GetEntries(LogLevel.Warning, null, "not saved", null, 10));
    }

    [Fact]
    public void WhenHostSystemIsSerializedAndReloaded_ThenLogsConfigurationIsNestedAndLogsIsCreatedWithTheStore()
    {
        // Arrange
        var store = new RecentLogStore();
        var serializer = CreateSerializer(store);
        var source = CreateHost(store);
        source.Logs.MinimumLevel = LogLevel.Error;
        source.Logs.Capacity = 5;
        var target = CreateHost(store);
        _ = new TestHostConfigurationWriter(CreateRegistryContext()) { Host = target };
        store.Add("Category", LogLevel.Critical, default, "Critical", null);

        // Act
        var json = serializer.Serialize(source);
        serializer.UpdateConfiguration(target, json);

        // Assert
        Assert.Contains("\"logs\"", json);
        Assert.Equal(LogLevel.Error, target.Logs.MinimumLevel);
        Assert.Equal(5, target.Logs.Capacity);
        Assert.Equal(1, target.Logs.TotalErrors);
    }

    [Fact]
    public void WhenLogsIsSetToNull_ThenTheCurrentLogsIsKept()
    {
        // Arrange
        var host = CreateHost(new RecentLogStore());
        var logs = host.Logs;

        // Act
        host.Logs = null!;

        // Assert
        Assert.Same(logs, host.Logs);
    }

    [Fact]
    public async Task WhenReplacedLogsAppliesItsConfiguration_ThenTheStoreKeepsTheCurrentConfiguration()
    {
        // Arrange
        var store = new RecentLogStore();
        var host = CreateHost(store);
        var replaced = host.Logs;
        replaced.Capacity = 5;
        host.Logs = new HostLogs(store) { Capacity = 7 };
        await host.ApplyConfigurationAsync(CancellationToken.None);

        // Act
        await replaced.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(7, store.Capacity);
    }

    [Fact]
    public async Task WhenLogLevelIsChangedOnReplacedLogs_ThenItThrowsAndTheStoreIsUnchanged()
    {
        // Arrange
        var store = new RecentLogStore();
        var host = CreateHost(store);
        var replaced = host.Logs;
        host.Logs = new HostLogs(store);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => replaced.ChangeLogLevelAsync(LogLevel.Debug, CancellationToken.None));
        Assert.Equal(LogLevel.Warning, store.MinimumLevel);
    }

    [Fact]
    public async Task WhenHostSystemRuns_ThenLogsConfigurationIsAppliedAndLogsAreUpdated()
    {
        // Arrange
        var store = new RecentLogStore();
        var host = CreateHost(store);
        host.Logs.Capacity = 7;
        store.Add("Category", LogLevel.Error, default, "Error", null);

        // Act
        await host.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => store.Capacity == 7 && host.Logs.TotalErrors == 1, message: "Logs were not applied and updated");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        // Assert
        Assert.Equal(1, host.Logs.EntryCount);
    }

    private static HostSystem CreateHost(RecentLogStore store)
    {
        return new HostSystem(null, () => { }, NullLogger.Instance, store)
        {
            PollingInterval = TimeSpan.FromHours(1)
        };
    }

    private static ConfigurableSubjectSerializer CreateSerializer(RecentLogStore store)
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(HostSystem), typeof(HostLogs)]);
        var services = new ServiceCollection();
        services.AddSingleton(store);
        return new ConfigurableSubjectSerializer(typeProvider, services.BuildServiceProvider());
    }

    // The serializer finds the configuration properties of a registered subject by their attributes in the registry,
    // and a nested subject finds its configuration writer through its parents.
    private static IInterceptorSubjectContext CreateRegistryContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithParents()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);
}

[InterceptorSubject]
public partial class TestHostConfigurationWriter : IConfigurationWriter
{
    public bool IsWriting { get; set; }

    public List<IInterceptorSubject> WrittenSubjects { get; } = [];

    public partial HostSystem? Host { get; set; }

    public Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        WrittenSubjects.Add(subject);
        return Task.FromResult(IsWriting);
    }
}
