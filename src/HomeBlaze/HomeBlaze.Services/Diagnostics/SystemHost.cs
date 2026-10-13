using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;
using DiagnosticsProcess = System.Diagnostics.Process;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// The machine HomeBlaze runs on, with the HomeBlaze process as <see cref="Process"/>.
/// </summary>
[Category("System")]
[Description("Reports CPU, memory and uptime of the machine and the process HomeBlaze runs in")]
[DisplayName("System host")]
[SubjectIcon("Computer")]
[SingleInstance]
[InterceptorSubject]
public partial class SystemHost : BackgroundService,
    IConfigurable,
    IMonitoredService,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    private static readonly TimeSpan MinimumPollingInterval = TimeSpan.FromSeconds(1);

    // Timers run on a coarse clock and can fire a few milliseconds before the Stopwatch measures
    // RateCalculator.MinimumElapsed, which would skip the first rates until a full polling interval passed.
    private static readonly TimeSpan FirstRateDelay = RateCalculator.MinimumElapsed + TimeSpan.FromMilliseconds(100);

    private readonly ISystemMetricsReader? _reader;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _pollSignal = new(0, 1);

    private bool _hasCpuBaseline;
    private long _cpuBaselineTimestamp;
    private ulong _cpuBaselineBusyTime;
    private ulong _cpuBaselineTotalTime;
    private bool _hasReadContainerMemoryLimit;

    [Configuration]
    public partial string Name { get; set; }

    [Configuration]
    public partial TimeSpan PollingInterval { get; set; }

    [State(Position = 400)]
    public partial string MachineName { get; internal set; }

    [State(Position = 401)]
    public partial string OperatingSystemName { get; internal set; }

    [State(Position = 402)]
    public partial string Architecture { get; internal set; }

    /// <summary>
    /// Processors available to the process, honouring a container CPU quota and affinity.
    /// </summary>
    [State(Position = 403)]
    public partial int ProcessorCount { get; internal set; }

    [State(IsDiscrete = true, Position = 404)]
    public partial bool IsContainer { get; internal set; }

    /// <summary>
    /// CPU usage of the whole host as a fraction of all its CPUs; inside a container this is the host, not the container.
    /// </summary>
    [State(Unit = StateUnit.Percent, Position = 410)]
    public partial decimal? CpuUsage { get; internal set; }

    [State(Unit = StateUnit.Byte, Position = 411)]
    public partial long? TotalMemory { get; internal set; }

    [State(Unit = StateUnit.Byte, Position = 412)]
    public partial long? AvailableMemory { get; internal set; }

    [Derived]
    [State(Unit = StateUnit.Percent, Position = 413)]
    public decimal? MemoryUsage =>
        TotalMemory is { } total && total > 0 && AvailableMemory is { } available
            ? Math.Round((decimal)(total - available) / total, 3)
            : null;

    /// <summary>
    /// Time since boot. Excludes suspended time on Linux and macOS and includes it on Windows.
    /// </summary>
    [State(Position = 414)]
    public partial TimeSpan? Uptime { get; internal set; }

    [State(Unit = StateUnit.Byte, Position = 420)]
    public partial long? ContainerMemoryLimit { get; internal set; }

    /// <summary>
    /// Container CPU limit in cores.
    /// </summary>
    [State(Position = 421)]
    public partial decimal? ContainerCpuLimit { get; internal set; }

    [State(Position = 430)]
    public partial HostProcess Process { get; internal set; }

    [State(IsDiscrete = true)]
    public partial ServiceStatus Status { get; internal set; }

    [State]
    public partial string? StatusMessage { get; internal set; }

    [State(Position = 950)]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    [Derived]
    public string? Title => string.IsNullOrEmpty(Name) ? MachineName : Name;

    [Derived]
    public string IconName => "Computer";

    [Derived]
    public string? IconColor => Status == ServiceStatus.Error ? "Error" : null;

    public SystemHost(ILogger<SystemHost> logger)
        : this(SystemMetricsReaderFactory.CreateForCurrentPlatform(), HostProcess.CollectAllGarbage, logger)
    {
    }

    internal SystemHost(ISystemMetricsReader? reader, Action collectGarbage, ILogger logger)
    {
        _reader = reader;
        _logger = logger;

        Name = string.Empty;
        PollingInterval = TimeSpan.FromSeconds(15);

        MachineName = Environment.MachineName;
        OperatingSystemName = SystemInformation.ReadOperatingSystemName();
        Architecture = RuntimeInformation.OSArchitecture.ToString();
        ProcessorCount = Environment.ProcessorCount;
        IsContainer = SystemInformation.IsContainer();
        ContainerCpuLimit = SystemInformation.ReadContainerCpuLimit();
        ContainerMemoryLimit = null;

        CpuUsage = null;
        TotalMemory = null;
        AvailableMemory = null;
        Uptime = null;

        Process = new HostProcess(collectGarbage, RequestPoll);

        Status = ServiceStatus.Stopped;
        StatusMessage = null;
        LastUpdated = null;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        RequestPoll();
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Detaching a subject only stops it and never disposes it, so the process handle lives in this method.
        using var currentProcess = DiagnosticsProcess.GetCurrentProcess();

        Poll(currentProcess);

        // The second poll follows shortly after the minimum rate window so rates appear without waiting a full interval.
        var delay = FirstRateDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _pollSignal.WaitAsync(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            Poll(currentProcess);
            delay = PollingInterval < MinimumPollingInterval ? MinimumPollingInterval : PollingInterval;
        }

        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }

    private void Poll(DiagnosticsProcess currentProcess)
    {
        try
        {
            var timestamp = Stopwatch.GetTimestamp();
            if (_reader is not null)
            {
                UpdateHost(_reader.Read(), timestamp);
            }

            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
            Process.Update(currentProcess, timestamp, ProcessorCount);
            LastUpdated = DateTimeOffset.UtcNow;

            if (Status != ServiceStatus.Running)
            {
                StatusMessage = null;
                Status = ServiceStatus.Running;
            }
        }
        catch (Exception exception)
        {
            if (Status != ServiceStatus.Error)
            {
                _logger.LogWarning(exception, "Reading the system host metrics failed");
            }

            // The message is written first so an observer that sees Error also sees its message.
            StatusMessage = exception.Message;
            Status = ServiceStatus.Error;
        }
    }

    private void UpdateHost(SystemMetricsReading reading, long timestamp)
    {
        TotalMemory = reading.TotalMemory;
        AvailableMemory = reading.AvailableMemory;

        if (!_hasReadContainerMemoryLimit)
        {
            ContainerMemoryLimit = SystemInformation.ReadContainerMemoryLimit(reading.TotalMemory);
            _hasReadContainerMemoryLimit = true;
        }

        if (_hasCpuBaseline)
        {
            if (Stopwatch.GetElapsedTime(_cpuBaselineTimestamp, timestamp) < RateCalculator.MinimumElapsed)
            {
                return;
            }

            CpuUsage = RateCalculator.ComputeCpuFraction(
                _cpuBaselineBusyTime, _cpuBaselineTotalTime,
                reading.CpuBusyTime, reading.CpuTotalTime) ?? CpuUsage;
        }

        _hasCpuBaseline = true;
        _cpuBaselineTimestamp = timestamp;
        _cpuBaselineBusyTime = reading.CpuBusyTime;
        _cpuBaselineTotalTime = reading.CpuTotalTime;
    }

    private void RequestPoll()
    {
        try
        {
            _pollSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A poll is already pending.
        }
    }
}
