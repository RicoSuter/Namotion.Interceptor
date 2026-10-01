using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Networking;
using HomeBlaze.Abstractions.Sensors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Luxtronik.Gating;
using Namotion.Devices.Luxtronik.Model;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Modbus.Client;

namespace Namotion.Devices.Luxtronik;

/// <summary>
/// A Luxtronik 2.1 heat pump read through the Smart Home Interface (Modbus TCP, port 502). Read only.
/// </summary>
[Category("Devices")]
[Description("Luxtronik 2.1 heat pump (Alpha Innotec, Novelan) via the Smart Home Interface, read only")]
[InterceptorSubject]
[System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S1200", Justification = "A device root aggregates its function subjects and the capability interfaces it implements; splitting it would only spread the same dependencies across files.")]
public partial class LuxtronikHeatPump :
    BackgroundService,
    IModbusDiscovery,
    IConfigurable,
    IPowerSensor,
    IThermalPowerSensor,
    IConnectionState,
    ISoftwareState,
    IMonitoredService,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    /// <summary>
    /// The minimum <see cref="PollingInterval"/> in seconds; shorter intervals are raised to it.
    /// </summary>
    public const int MinimumPollingIntervalSeconds = 10;

    private const int UnknownFunctionMask = -1;

    private readonly ILogger<LuxtronikHeatPump> _logger;
    private readonly SemaphoreSlim _configurationChanged = new(0, 1);

    // Written by the discovery on the source's thread, read by the status loop.
    private int _discoveredFunctionMask = UnknownFunctionMask;

    // Protects the controller from a hand-edited configuration that would poll it continuously; only tests lower it.
    internal TimeSpan MinimumPollingInterval { get; init; } = TimeSpan.FromSeconds(MinimumPollingIntervalSeconds);

    /// <summary>
    /// Gets how often <see cref="DiscoverAsync"/> ran.
    /// </summary>
    internal int DiscoveryCount { get; private set; }

    [Configuration]
    public partial string Name { get; set; }

    [Configuration]
    public partial string? HostAddress { get; set; }

    [Configuration]
    public partial int Port { get; set; }

    /// <summary>
    /// Gets or sets the interval between reads of the controller. Values below <see cref="MinimumPollingIntervalSeconds"/> are raised to it.
    /// </summary>
    [Configuration]
    public partial TimeSpan PollingInterval { get; set; }

    [State(IsDiscrete = true)]
    public partial bool IsConnected { get; internal set; }

    /// <summary>
    /// Gets the connection status. A non-zero controller error number is reported in <see cref="StatusMessage"/> while the
    /// connection is healthy, and the status stays <see cref="ServiceStatus.Running"/>.
    /// </summary>
    [State(IsDiscrete = true)]
    public partial ServiceStatus Status { get; internal set; }

    [State]
    public partial string? StatusMessage { get; internal set; }

    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    /// <summary>
    /// Gets the controller firmware, such as "3.92.3", read by the discovery on every connect.
    /// </summary>
    [State]
    public partial string? SoftwareVersion { get; internal set; }

    /// <summary>
    /// Gets the operating state of the heat pump as a whole.
    /// </summary>
    [State(Position = 10)]
    public partial LuxtronikOperatingStatus OperatingStatus { get; internal set; }

    /// <summary>
    /// Gets the temperatures of the heat pump itself.
    /// </summary>
    [State(Position = 11)]
    public partial LuxtronikTemperatures Temperatures { get; internal set; }

    /// <summary>
    /// Gets the power and energy totals.
    /// </summary>
    [State(Position = 12)]
    public partial LuxtronikEnergy Energy { get; internal set; }

    /// <summary>
    /// Gets the Smart Grid (EVU) signals.
    /// </summary>
    [State(Position = 13)]
    public partial LuxtronikSmartGrid SmartGrid { get; internal set; }

    /// <summary>
    /// Gets the electrical power consumption limit.
    /// </summary>
    [State(Position = 14)]
    public partial LuxtronikPowerConsumptionLimit PowerConsumptionLimit { get; internal set; }

    /// <summary>
    /// Gets which functions are active; the optional function subjects follow these flags.
    /// </summary>
    [State(Position = 15)]
    public partial LuxtronikFunctions Functions { get; internal set; }

    /// <summary>
    /// Gets heating.
    /// </summary>
    [State(Position = 20)]
    public partial LuxtronikHeating Heating { get; internal set; }

    /// <summary>
    /// Gets hot water.
    /// </summary>
    [State(Position = 21)]
    public partial LuxtronikHotWater HotWater { get; internal set; }

    /// <summary>
    /// Gets cooling, or <c>null</c> while its flag is clear.
    /// </summary>
    [State(Position = 22)]
    public partial LuxtronikCooling? Cooling { get; internal set; }

    /// <summary>
    /// Gets pool heating, or <c>null</c> while its flag is clear.
    /// </summary>
    [State(Position = 23)]
    public partial LuxtronikPool? Pool { get; internal set; }

    /// <summary>
    /// Gets solar, or <c>null</c> while its flag is clear.
    /// </summary>
    [State(Position = 24)]
    public partial LuxtronikSolar? Solar { get; internal set; }

    /// <summary>
    /// Gets the room control unit, or <c>null</c> while its flag is clear.
    /// </summary>
    [State(Position = 25)]
    public partial LuxtronikRoomControl? RoomControl { get; internal set; }

    /// <summary>
    /// Gets mixing circuit 1, or <c>null</c> while neither its heating nor its cooling flag is set.
    /// </summary>
    [State(Position = 26)]
    public partial LuxtronikMixingCircuit? MixingCircuit1 { get; internal set; }

    /// <summary>
    /// Gets mixing circuit 2, or <c>null</c> while neither its heating nor its cooling flag is set.
    /// </summary>
    [State(Position = 27)]
    public partial LuxtronikMixingCircuit? MixingCircuit2 { get; internal set; }

    /// <summary>
    /// Gets mixing circuit 3, or <c>null</c> while neither its heating nor its cooling flag is set.
    /// </summary>
    [State(Position = 28)]
    public partial LuxtronikMixingCircuit? MixingCircuit3 { get; internal set; }

    [Derived]
    public decimal? Power => Energy.ElectricalPower;

    [Derived]
    public decimal? EnergyConsumed => Energy.TotalElectricalEnergy;

    [Derived]
    public decimal? ThermalPower => Energy.ThermalPower;

    [Derived]
    public decimal? ThermalEnergyProduced => Energy.TotalThermalEnergy;

    [Derived]
    [State]
    public string? AvailableSoftwareUpdate => null;

    [Derived]
    public string Title => string.IsNullOrEmpty(Name) ? "Luxtronik Heat Pump" : Name;

    public string IconName => "HeatPump";

    [Derived]
    public string? IconColor => IsConnected ? "Success" : null;

    public LuxtronikHeatPump(ILogger<LuxtronikHeatPump> logger)
    {
        _logger = logger;

        Name = string.Empty;
        HostAddress = null;
        Port = 502;
        PollingInterval = TimeSpan.FromSeconds(30);

        IsConnected = false;
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
        LastUpdated = null;
        SoftwareVersion = null;

        OperatingStatus = new LuxtronikOperatingStatus();
        Temperatures = new LuxtronikTemperatures();
        Energy = new LuxtronikEnergy();
        SmartGrid = new LuxtronikSmartGrid();
        PowerConsumptionLimit = new LuxtronikPowerConsumptionLimit();
        Functions = new LuxtronikFunctions();
        Heating = new LuxtronikHeating();
        HotWater = new LuxtronikHotWater();
        Cooling = null;
        Pool = null;
        Solar = null;
        RoomControl = null;
        MixingCircuit1 = null;
        MixingCircuit2 = null;
        MixingCircuit3 = null;
    }

    /// <summary>
    /// Reads the firmware version and active functions, creates or removes the optional function subjects, and excludes the registers the controller does not provide.
    /// </summary>
    public async Task DiscoverAsync(ModbusDiscoveryContext context, CancellationToken cancellationToken)
    {
        DiscoveryCount++;

        // A reconnect writes the new flags before publishing their mask, so the stale mask would restart the source meanwhile.
        Volatile.Write(ref _discoveredFunctionMask, UnknownFunctionMask);

        var functionMask = await LuxtronikDiscovery.DiscoverAsync(this, context, _logger, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _discoveredFunctionMask, functionMask ?? UnknownFunctionMask);
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        try
        {
            _configurationChanged.Release();
        }
        catch (SemaphoreFullException)
        {
            // A change is already signaled and not yet consumed, which covers this one.
        }
        catch (ObjectDisposedException)
        {
            // The device is disposed, so there is no source left to restart.
        }

        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var hostAddress = HostAddress;
            if (string.IsNullOrWhiteSpace(hostAddress))
            {
                Status = ServiceStatus.Stopped;
                StatusMessage = "No host address configured";
                await WaitForConfigurationChangeAsync(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
                continue;
            }

            await RunSourceAsync(hostAddress, stoppingToken).ConfigureAwait(false);
        }

        IsConnected = false;
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }

    /// <summary>
    /// Mirrors the source diagnostics into <see cref="IsConnected"/>, <see cref="Status"/>, <see cref="StatusMessage"/>
    /// and <see cref="LastUpdated"/>. Disconnected reads as <see cref="ServiceStatus.Error"/> once the source reported an
    /// error, which stays set while it reconnects. While the connection is healthy, a non-zero controller error number is
    /// reported in <see cref="StatusMessage"/> and the status stays <see cref="ServiceStatus.Running"/>.
    /// </summary>
    internal void UpdateStatus(ModbusClientDiagnostics diagnostics)
    {
        IsConnected = diagnostics.IsOperational == true;
        LastUpdated = diagnostics.Polling.LastPollTime ?? LastUpdated;

        if (IsConnected)
        {
            var errorNumber = OperatingStatus.ErrorNumber;
            Status = ServiceStatus.Running;
            StatusMessage = errorNumber is > 0 ? $"Heat pump error {errorNumber}" : null;
        }
        else
        {
            var lastError = diagnostics.LastError;
            Status = lastError is null ? ServiceStatus.Starting : ServiceStatus.Error;
            StatusMessage = lastError?.Message ?? "Connecting...";
        }
    }

    /// <summary>
    /// Gets the configured <see cref="PollingInterval"/>, raised to <see cref="MinimumPollingInterval"/>.
    /// </summary>
    internal TimeSpan GetEffectivePollingInterval()
    {
        var pollingInterval = PollingInterval;
        return pollingInterval < MinimumPollingInterval ? MinimumPollingInterval : pollingInterval;
    }

    private async Task RunSourceAsync(string hostAddress, CancellationToken stoppingToken)
    {
        // Captured once, so a configuration edit applies only through the restart it signals.
        var pollingInterval = GetEffectivePollingInterval();

        ModbusSubjectClientSource source;
        try
        {
            source = this.CreateModbusClientSource(
                new ModbusClientConfiguration { Host = hostAddress, Port = Port, PollingInterval = pollingInterval },
                _logger);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            _logger.LogError(exception, "Luxtronik heat pump {HostAddress} has an invalid configuration.", hostAddress);
            Status = ServiceStatus.Error;
            StatusMessage = exception.Message;
            await WaitForConfigurationChangeAsync(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
            return;
        }

        // Discards the previous source's discovery, whose mismatch with the polled flags would restart this source again.
        Volatile.Write(ref _discoveredFunctionMask, UnknownFunctionMask);

        var hasFailed = false;
        try
        {
            Status = ServiceStatus.Starting;
            StatusMessage = "Connecting...";

            await this.AttachHostedServiceAsync(source, stoppingToken).ConfigureAwait(false);

            // Source diagnostics are not tracked properties, so they are mirrored into this device's state.
            while (!stoppingToken.IsCancellationRequested)
            {
                UpdateStatus(source.Diagnostics);
                if (HaveFunctionsChanged(hostAddress) ||
                    await WaitForConfigurationChangeAsync(pollingInterval, stoppingToken).ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping: the source is released below.
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Luxtronik heat pump {HostAddress} failed.", hostAddress);
            Status = ServiceStatus.Error;
            StatusMessage = exception.Message;
            hasFailed = true;
        }
        finally
        {
            await ReleaseSourceAsync(source, hostAddress, stoppingToken).ConfigureAwait(false);
        }

        if (hasFailed)
        {
            await WaitForConfigurationChangeAsync(pollingInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets whether the polled <see cref="Functions"/> differ from the flags the last discovery read, which excluded the
    /// registers of the inactive functions. <c>false</c> until both are known.
    /// </summary>
    private bool HaveFunctionsChanged(string hostAddress)
    {
        var discoveredMask = Volatile.Read(ref _discoveredFunctionMask);

        // The final re-read drops a comparison against a mask a discovery replaced while the flags were read.
        if (discoveredMask == UnknownFunctionMask ||
            Functions.GetFunctionMask() is not { } polledMask ||
            polledMask == discoveredMask ||
            Volatile.Read(ref _discoveredFunctionMask) != discoveredMask)
        {
            return false;
        }

        _logger.LogInformation(
            "Luxtronik {HostAddress} changed its active functions ({ChangedFunctions}); discovering its registers again.",
            hostAddress, LuxtronikGating.GetFunctionNames(polledMask ^ discoveredMask));
        return true;
    }

    private async Task ReleaseSourceAsync(ModbusSubjectClientSource source, string hostAddress, CancellationToken stoppingToken)
    {
        try
        {
            await this.DetachHostedServiceAsync(source, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The hosting refuses detaching while the host stops, and then stops the source itself.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to detach the Modbus source of {HostAddress}.", hostAddress);
        }

        try
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to dispose the Modbus source of {HostAddress}.", hostAddress);
        }

        IsConnected = false;
    }

    private async Task<bool> WaitForConfigurationChangeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await _configurationChanged.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _configurationChanged.Dispose();
    }
}
