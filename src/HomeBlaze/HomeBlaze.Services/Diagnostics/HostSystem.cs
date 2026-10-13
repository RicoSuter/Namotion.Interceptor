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
[DisplayName("Host system")]
[SubjectIcon("Computer")]
[SingleInstance]
[InterceptorSubject]
public partial class HostSystem : BackgroundService,
    IConfigurable,
    IMonitoredService,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider,
    IUptimeState
{
    private static readonly TimeSpan MinimumPollingInterval = TimeSpan.FromSeconds(1);

    // SemaphoreSlim.WaitAsync(TimeSpan) throws above int.MaxValue milliseconds (about 24.8 days).
    private static readonly TimeSpan MaximumPollingDelay = TimeSpan.FromMilliseconds(int.MaxValue);

    private readonly ISystemMetricsReader? _reader;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _pollSignal = new(0, 1);

    private RateWindow _cpuWindow;
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

    public HostSystem(ILogger<HostSystem> logger)
        : this(SystemMetricsReaderFactory.CreateForCurrentPlatform(), HostProcess.CollectAllGarbage, logger)
    {
    }

    internal HostSystem(ISystemMetricsReader? reader, Action collectGarbage, ILogger logger)
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

        _cpuWindow.Reset();
        Process.ResetBaseline();

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = Poll(currentProcess) ? GetPollingDelay() : GetRateWindowDelay();

            try
            {
                await _pollSignal.WaitAsync(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }

    /// <summary>
    /// Refreshes the values and returns false when a rate window is still open, so a rate was not computed.
    /// </summary>
    private bool Poll(DiagnosticsProcess currentProcess)
    {
        var timestamp = Stopwatch.GetTimestamp();
        Exception? error = null;
        var hasUpdated = false;
        var areRatesComputed = true;

        if (_reader is not null)
        {
            try
            {
                areRatesComputed = UpdateHost(_reader.Read(), timestamp);
                hasUpdated = true;
            }
            catch (Exception exception)
            {
                error = exception;
            }
        }

        Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);

        try
        {
            areRatesComputed &= Process.Update(currentProcess, timestamp, ProcessorCount);
            hasUpdated = true;
        }
        catch (Exception exception)
        {
            error ??= exception;
        }

        if (hasUpdated)
        {
            LastUpdated = DateTimeOffset.UtcNow;
        }

        if (error is null)
        {
            if (Status != ServiceStatus.Running)
            {
                StatusMessage = null;
                Status = ServiceStatus.Running;
            }

            return areRatesComputed;
        }

        if (Status != ServiceStatus.Error)
        {
            _logger.LogWarning(error, "Reading the host system metrics failed");
        }

        // The message is written first so an observer that sees Error also sees its message.
        StatusMessage = error.Message;
        Status = ServiceStatus.Error;
        return areRatesComputed;
    }

    /// <summary>
    /// Refreshes the host values and returns whether the CPU usage was computed.
    /// </summary>
    private bool UpdateHost(SystemMetricsReading reading, long timestamp)
    {
        TotalMemory = reading.TotalMemory;
        AvailableMemory = reading.AvailableMemory;

        if (!_hasReadContainerMemoryLimit)
        {
            ContainerMemoryLimit = SystemInformation.ReadContainerMemoryLimit(reading.TotalMemory);
            _hasReadContainerMemoryLimit = true;
        }

        var hadBaseline = _cpuWindow.HasBaseline;
        var isAdvanced = _cpuWindow.TryAdvance(timestamp, out _);
        if (isAdvanced)
        {
            CpuUsage = RateCalculator.ComputeCpuFraction(
                _cpuBaselineBusyTime, _cpuBaselineTotalTime,
                reading.CpuBusyTime, reading.CpuTotalTime) ?? CpuUsage;
        }

        if (isAdvanced || !hadBaseline)
        {
            _cpuBaselineBusyTime = reading.CpuBusyTime;
            _cpuBaselineTotalTime = reading.CpuTotalTime;
        }

        return isAdvanced;
    }

    private TimeSpan GetPollingDelay()
    {
        if (PollingInterval < MinimumPollingInterval)
        {
            return MinimumPollingInterval;
        }

        return PollingInterval > MaximumPollingDelay ? MaximumPollingDelay : PollingInterval;
    }

    private TimeSpan GetRateWindowDelay()
    {
        // Waits for the longer window, so both pass on the next poll, and until the minimum polling interval
        // passed since its baseline, so a timer that fires a few milliseconds early still lands after it.
        var timestamp = Stopwatch.GetTimestamp();
        var cpuRemaining = _cpuWindow.Remaining(timestamp);
        var processRemaining = Process.GetRemainingRateWindow(timestamp);
        var remaining = cpuRemaining > processRemaining ? cpuRemaining : processRemaining;
        return remaining + (MinimumPollingInterval - RateWindow.MinimumElapsed);
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
