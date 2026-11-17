using HomeBlaze.Services.Diagnostics;
using HomeBlaze.Storage.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace HomeBlaze.Services.Tests.Diagnostics;

public class HostLogsTests
{
    [Fact]
    public void WhenHostSystemIsCreated_ThenLogsReportTheStoreAndConfigurationHasDefaults()
    {
        // Arrange
        var store = new RecentLogStore();

        // Act
        var host = CreateHost(store);

        // Assert
        Assert.Null(host.RecentLogMinimumLevel);
        Assert.Equal(1000, host.RecentLogCapacity);
        Assert.Equal("Logs", host.Logs.Title);
        Assert.Equal(LogLevel.Warning, host.Logs.MinimumLevel);
        Assert.Equal(1000, host.Logs.Capacity);
        Assert.Equal(0, host.Logs.TotalWarnings);
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
    public async Task WhenConfigurationIsApplied_ThenStoreAndLogsFollowIt()
    {
        // Arrange
        var store = new RecentLogStore();
        var host = CreateHost(store);
        host.RecentLogMinimumLevel = LogLevel.Information;
        host.RecentLogCapacity = 5;

        // Act
        await host.ApplyConfigurationAsync(CancellationToken.None);
        host.Logs.Update();

        // Assert
        Assert.Equal(LogLevel.Information, store.MinimumLevel);
        Assert.Equal(5, store.Capacity);
        Assert.Equal(LogLevel.Information, host.Logs.MinimumLevel);
        Assert.Equal(5, host.Logs.Capacity);
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
        var host = CreateHost(store);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => host.Logs.ChangeLogLevelAsync(level, CancellationToken.None));
        Assert.Null(host.RecentLogMinimumLevel);
        Assert.Equal(LogLevel.Warning, store.MinimumLevel);
    }

    [Fact]
    public async Task WhenLogLevelIsChanged_ThenItIsAppliedAndSaved()
    {
        // Arrange
        var store = new RecentLogStore();
        var host = CreateHost(store);
        var context = InterceptorSubjectContext.Create().WithParents();
        var writer = new TestHostConfigurationWriter(context) { IsWriting = true, Host = host };

        // Act
        await host.Logs.ChangeLogLevelAsync(LogLevel.Debug, CancellationToken.None);

        // Assert
        Assert.Equal(LogLevel.Debug, host.RecentLogMinimumLevel);
        Assert.Equal(LogLevel.Debug, store.MinimumLevel);
        Assert.Same(host, Assert.Single(writer.WrittenSubjects));
    }

    [Fact]
    public async Task WhenLogLevelIsChangedToNull_ThenTheDefaultIsRestored()
    {
        // Arrange
        var store = new RecentLogStore();
        var host = CreateHost(store);
        await host.Logs.ChangeLogLevelAsync(LogLevel.Error, CancellationToken.None);

        // Act
        await host.Logs.ChangeLogLevelAsync(null, CancellationToken.None);

        // Assert
        Assert.Null(host.RecentLogMinimumLevel);
        Assert.Equal(LogLevel.Warning, store.MinimumLevel);
    }

    [Fact]
    public async Task WhenLogLevelIsChangedButNotSaved_ThenWarningIsLogged()
    {
        // Arrange
        var store = new RecentLogStore();
        using var provider = new RecentLogLoggerProvider(store);
        var host = new HostSystem(null, () => { }, provider.CreateLogger("HostSystem"), store);
        var context = InterceptorSubjectContext.Create().WithParents();
        _ = new TestHostConfigurationWriter(context) { IsWriting = false, Host = host };

        // Act
        await host.Logs.ChangeLogLevelAsync(LogLevel.Debug, CancellationToken.None);

        // Assert
        Assert.Contains(store.GetEntries(LogLevel.Warning, null, "not saved", null, 10), entry => entry.Category == "HostSystem");
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
    public async Task WhenHostSystemRuns_ThenLogsAreUpdatedByThePollingLoop()
    {
        // Arrange
        var store = new RecentLogStore();
        var host = CreateHost(store);
        host.RecentLogCapacity = 7;
        store.Add("Category", LogLevel.Error, default, "Error", null);

        // Act
        await host.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => host.Logs.TotalErrors == 1 && host.Logs.Capacity == 7, message: "Logs were not updated");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        // Assert
        Assert.Equal(7, store.Capacity);
    }

    private static HostSystem CreateHost(RecentLogStore store)
    {
        return new HostSystem(null, () => { }, NullLogger.Instance, store)
        {
            PollingInterval = TimeSpan.FromHours(1)
        };
    }
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
