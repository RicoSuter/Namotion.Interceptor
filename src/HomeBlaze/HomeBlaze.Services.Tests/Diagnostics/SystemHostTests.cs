using HomeBlaze.Abstractions;
using HomeBlaze.Services.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.Services.Tests.Diagnostics;

public class SystemHostTests
{
    [Fact]
    public void WhenCreated_ThenHasDefaultsAndStaticValues()
    {
        // Act
        var host = new SystemHost(NullLogger<SystemHost>.Instance);

        // Assert
        Assert.Equal(string.Empty, host.Name);
        Assert.Equal(TimeSpan.FromSeconds(15), host.PollingInterval);
        Assert.Equal(Environment.MachineName, host.MachineName);
        Assert.Equal(Environment.MachineName, host.Title);
        Assert.Equal(Environment.ProcessorCount, host.ProcessorCount);
        Assert.False(string.IsNullOrWhiteSpace(host.OperatingSystemName));
        Assert.False(string.IsNullOrWhiteSpace(host.Process.RuntimeVersion));
        Assert.Equal(ServiceStatus.Stopped, host.Status);
        Assert.Null(host.CpuUsage);
    }

    [Fact]
    public void WhenNameIsSet_ThenTitleUsesName()
    {
        // Arrange
        var host = new SystemHost(NullLogger<SystemHost>.Instance);

        // Act
        host.Name = "Server";

        // Assert
        Assert.Equal("Server", host.Title);
    }

    [Fact]
    public async Task WhenRunningOnTheCurrentPlatform_ThenRatesAppearInRange()
    {
        // Arrange
        var host = new SystemHost(NullLogger<SystemHost>.Instance);
        var supported = OperatingSystem.IsLinux() || OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

        // Act
        await host.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => host.Process.CpuUsage is not null && (!supported || host.CpuUsage is not null),
                message: "Rates did not appear");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        // Assert
        Assert.InRange(host.Process.CpuUsage!.Value, 0m, 1m);
        Assert.InRange(host.Process.GcPauseTime!.Value, 0m, 1m);
        Assert.NotNull(host.Process.AllocationRate);
        Assert.True(host.Process.WorkingSet > 0);
        Assert.True(host.Process.ThreadCount > 0);
        if (supported)
        {
            Assert.InRange(host.CpuUsage!.Value, 0m, 1m);
            Assert.InRange(host.MemoryUsage!.Value, 0m, 1m);
            Assert.True(host.TotalMemory > 0);
        }
    }

    [Fact]
    public async Task WhenWokenEarly_ThenKeepsPreviousCpuUsage()
    {
        // Arrange: the third reading would give 100 % if it were used
        var reader = new FakeSystemMetricsReader(
            new SystemMetricsReading(0, 0, 1000, 500),
            new SystemMetricsReading(50, 100, 1000, 500),
            new SystemMetricsReading(150, 200, 1000, 500));
        var host = new SystemHost(reader, () => { }, NullLogger.Instance) { PollingInterval = TimeSpan.FromHours(1) };

        await host.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => host.CpuUsage is not null, message: "CPU usage did not appear");

            // Act
            await host.ApplyConfigurationAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(() => reader.ReadCount == 3, message: "Early poll did not run");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        // Assert
        Assert.Equal(0.5m, host.CpuUsage);
        Assert.Equal(0.5m, host.MemoryUsage);
    }

    [Fact]
    public async Task WhenCollectGarbageIsInvoked_ThenCollectsAndPollsImmediately()
    {
        // Arrange
        var reader = new FakeSystemMetricsReader(new SystemMetricsReading(0, 100, 1000, 500));
        var collectCount = 0;
        var host = new SystemHost(reader, () => Interlocked.Increment(ref collectCount), NullLogger.Instance)
        {
            PollingInterval = TimeSpan.FromHours(1)
        };

        await host.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => reader.ReadCount == 2, message: "Initial polls did not run");

            // Act
            host.Process.CollectGarbage();
            await AsyncTestHelpers.WaitUntilAsync(() => reader.ReadCount == 3, message: "Poll after collect did not run");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        // Assert
        Assert.Equal(1, collectCount);
    }

    [Fact]
    public async Task WhenReaderFails_ThenStatusIsError()
    {
        // Arrange
        var reader = new FakeSystemMetricsReader();
        var host = new SystemHost(reader, () => { }, NullLogger.Instance) { PollingInterval = TimeSpan.FromHours(1) };

        // Act
        string? statusMessage;
        string? iconColor;
        await host.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => host.Status == ServiceStatus.Error, message: "Status did not become Error");

            // Stopping resets the status, so the values are captured while the loop runs.
            statusMessage = host.StatusMessage;
            iconColor = host.IconColor;
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        // Assert
        Assert.Equal("No reading", statusMessage);
        Assert.Equal("Error", iconColor);
    }

    [Fact]
    public async Task WhenReaderFails_ThenProcessValuesStillUpdate()
    {
        // Arrange
        var reader = new FakeSystemMetricsReader();
        var host = new SystemHost(reader, () => { }, NullLogger.Instance) { PollingInterval = TimeSpan.FromHours(1) };

        // Act
        await host.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => host.Status == ServiceStatus.Error && host.Process.WorkingSet is not null && host.LastUpdated is not null,
                message: "Process values did not update while the reader failed");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        // Assert
        Assert.True(host.Process.WorkingSet > 0);
        Assert.Null(host.TotalMemory);
    }

    [Fact]
    public async Task WhenRestarted_ThenFirstPollIsANewBaseline()
    {
        // Arrange: computing the third reading against the second would give 100 %
        var reader = new FakeSystemMetricsReader(
            new SystemMetricsReading(0, 0, 1000, 500),
            new SystemMetricsReading(50, 100, 1000, 500),
            new SystemMetricsReading(150, 200, 1000, 500));
        var host = new SystemHost(reader, () => { }, NullLogger.Instance) { PollingInterval = TimeSpan.FromHours(1) };

        await host.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => host.CpuUsage is not null, message: "CPU usage did not appear");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        // Act
        await host.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => reader.ReadCount == 4, message: "Polls after restart did not run");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        // Assert
        Assert.Equal(0.5m, host.CpuUsage);
    }

    [Fact]
    public async Task WhenPollIsRequestedWhileStopped_ThenRestartStillComputesRates()
    {
        // Arrange
        var reader = new FakeSystemMetricsReader(
            new SystemMetricsReading(0, 0, 1000, 500),
            new SystemMetricsReading(50, 100, 1000, 500),
            new SystemMetricsReading(150, 200, 1000, 500),
            new SystemMetricsReading(175, 300, 1000, 500));
        var host = new SystemHost(reader, () => { }, NullLogger.Instance) { PollingInterval = TimeSpan.FromHours(1) };

        await host.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => host.CpuUsage is not null, message: "CPU usage did not appear");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        await host.ApplyConfigurationAsync(CancellationToken.None);

        // Act
        await host.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => host.CpuUsage == 0.25m, message: "CPU usage was not computed after restart");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        // Assert
        Assert.Equal(4, reader.ReadCount);
    }

    private sealed class FakeSystemMetricsReader(params SystemMetricsReading[] readings) : ISystemMetricsReader
    {
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public SystemMetricsReading Read()
        {
            var index = Interlocked.Increment(ref _readCount) - 1;
            if (readings.Length == 0)
            {
                throw new InvalidOperationException("No reading");
            }

            return readings[Math.Min(index, readings.Length - 1)];
        }
    }
}
