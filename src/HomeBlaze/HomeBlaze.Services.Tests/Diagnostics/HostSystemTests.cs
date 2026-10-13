using HomeBlaze.Abstractions;
using HomeBlaze.Services.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.Services.Tests.Diagnostics;

public class HostSystemTests
{
    [Fact]
    public void WhenCreated_ThenHasDefaultsAndStaticValues()
    {
        // Act
        var host = new HostSystem(NullLogger<HostSystem>.Instance);

        // Assert
        Assert.Equal(string.Empty, host.Name);
        Assert.Equal(TimeSpan.FromSeconds(15), host.PollingInterval);
        Assert.Equal(Environment.MachineName, host.MachineName);
        Assert.Equal(Environment.MachineName, host.Title);
        Assert.Equal(Environment.ProcessorCount, host.ProcessorCount);
        Assert.False(string.IsNullOrWhiteSpace(host.OperatingSystemName));
        Assert.False(string.IsNullOrWhiteSpace(host.Process.RuntimeVersion));
        Assert.Equal("Process", host.Process.Title);
        Assert.Equal("Memory", host.Process.IconName);
        Assert.Equal(ServiceStatus.Stopped, host.Status);
        Assert.Null(host.CpuUsage);
    }

    [Fact]
    public void WhenNameIsSet_ThenTitleUsesName()
    {
        // Arrange
        var host = new HostSystem(NullLogger<HostSystem>.Instance);

        // Act
        host.Name = "Server";

        // Assert
        Assert.Equal("Server", host.Title);
    }

    [Fact]
    public async Task WhenRunningOnTheCurrentPlatform_ThenRatesAppearInRange()
    {
        // Arrange
        var host = new HostSystem(NullLogger<HostSystem>.Instance);
        var supported = OperatingSystem.IsLinux() || OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

        // Act
        await RunAsync(host, () => AsyncTestHelpers.WaitUntilAsync(
            () => host.Process.CpuUsage is not null && (!supported || host.CpuUsage is not null),
            message: "Rates did not appear"));

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
        // Arrange: the third reading would give 100 % if it were used. The fourth repeats the second, so the
        // poll that follows the early one once the rate window passed computes nothing new.
        var reader = new FakeSystemMetricsReader(
            new SystemMetricsReading(0, 0, 1000, 500),
            new SystemMetricsReading(50, 100, 1000, 500),
            new SystemMetricsReading(150, 200, 1000, 500),
            new SystemMetricsReading(50, 100, 1000, 500));
        var host = CreateHost(reader);

        await RunAsync(host, async () =>
        {
            await AsyncTestHelpers.WaitUntilAsync(() => host.CpuUsage is not null, message: "CPU usage did not appear");

            // Act
            await host.ApplyConfigurationAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(() => reader.ReadCount >= 3, message: "Early poll did not run");
        });

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
        var host = CreateHost(reader, () => Interlocked.Increment(ref collectCount));

        await RunAsync(host, async () =>
        {
            await AsyncTestHelpers.WaitUntilAsync(() => reader.ReadCount == 2, message: "Initial polls did not run");

            // Act
            host.Process.CollectGarbage();
            await AsyncTestHelpers.WaitUntilAsync(() => reader.ReadCount >= 3, message: "Poll after collect did not run");
        });

        // Assert
        Assert.Equal(1, collectCount);
    }

    [Fact]
    public async Task WhenReaderFails_ThenStatusIsError()
    {
        // Arrange
        var host = CreateHost(new FakeSystemMetricsReader());
        string? statusMessage = null;
        string? iconColor = null;

        // Act
        await RunAsync(host, async () =>
        {
            await AsyncTestHelpers.WaitUntilAsync(() => host.Status == ServiceStatus.Error, message: "Status did not become Error");

            // Stopping resets the status, so the values are captured while the loop runs.
            statusMessage = host.StatusMessage;
            iconColor = host.IconColor;
        });

        // Assert
        Assert.Equal("No reading", statusMessage);
        Assert.Equal("Error", iconColor);
    }

    [Fact]
    public async Task WhenReaderFails_ThenProcessValuesStillUpdate()
    {
        // Arrange
        var host = CreateHost(new FakeSystemMetricsReader());

        // Act
        await RunAsync(host, () => AsyncTestHelpers.WaitUntilAsync(
            () => host.Status == ServiceStatus.Error && host.Process.WorkingSet is not null && host.LastUpdated is not null,
            message: "Process values did not update while the reader failed"));

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
        var host = CreateHost(reader);
        await RunAsync(host, () => AsyncTestHelpers.WaitUntilAsync(() => host.CpuUsage is not null, message: "CPU usage did not appear"));

        // Act
        await RunAsync(host, () => AsyncTestHelpers.WaitUntilAsync(() => reader.ReadCount == 4, message: "Polls after restart did not run"));

        // Assert
        Assert.Equal(0.5m, host.CpuUsage);
    }

    [Fact]
    public async Task WhenPollIsRequestedWhileStopped_ThenRestartStillComputesRates()
    {
        // Arrange: the requested poll wakes the restarted loop inside the rate window of the third reading,
        // and 25 % only appears when that early poll keeps the third reading as the baseline
        var reader = new FakeSystemMetricsReader(
            new SystemMetricsReading(0, 0, 1000, 500),
            new SystemMetricsReading(50, 100, 1000, 500),
            new SystemMetricsReading(150, 200, 1000, 500),
            new SystemMetricsReading(175, 300, 1000, 500));
        var host = CreateHost(reader);
        await RunAsync(host, () => AsyncTestHelpers.WaitUntilAsync(() => host.CpuUsage is not null, message: "CPU usage did not appear"));
        await host.ApplyConfigurationAsync(CancellationToken.None);

        // Act
        await RunAsync(host, () => AsyncTestHelpers.WaitUntilAsync(() => host.CpuUsage == 0.25m, message: "CPU usage was not computed after restart"));

        // Assert
        Assert.Equal(0.25m, host.CpuUsage);
    }

    [Fact]
    public async Task WhenPollingIntervalIsHuge_ThenLoopKeepsRunning()
    {
        // Arrange: int.MaxValue milliseconds is about 24.8 days, and SemaphoreSlim.WaitAsync(TimeSpan) throws
        // above that, so an unclamped delay would fault the loop as soon as it polls a second time.
        var reader = new FakeSystemMetricsReader(
            new SystemMetricsReading(0, 0, 1000, 500),
            new SystemMetricsReading(50, 100, 1000, 500),
            new SystemMetricsReading(100, 200, 1000, 500));
        var host = CreateHost(reader);
        host.PollingInterval = TimeSpan.FromDays(100);
        var statusWhileRunning = ServiceStatus.Stopped;

        // Act
        await RunAsync(host, async () =>
        {
            await AsyncTestHelpers.WaitUntilAsync(() => host.CpuUsage is not null, message: "CPU usage did not appear");
            statusWhileRunning = host.Status;

            // Wakes the loop while it should be waiting out the huge polling delay; it only responds if that wait did not fault.
            await host.ApplyConfigurationAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(() => reader.ReadCount >= 3, message: "Loop did not survive the huge polling delay");
        });

        // Assert
        Assert.Equal(ServiceStatus.Running, statusWhileRunning);
        Assert.Equal(ServiceStatus.Stopped, host.Status);
    }

    private static HostSystem CreateHost(FakeSystemMetricsReader reader, Action? collectGarbage = null)
    {
        return new HostSystem(reader, collectGarbage ?? (() => { }), NullLogger.Instance)
        {
            PollingInterval = TimeSpan.FromHours(1)
        };
    }

    private static async Task RunAsync(HostSystem host, Func<Task> whileRunning)
    {
        await host.StartAsync(CancellationToken.None);
        try
        {
            await whileRunning();
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
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
