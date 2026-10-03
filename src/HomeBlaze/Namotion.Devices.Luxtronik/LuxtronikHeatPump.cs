using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
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
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Modbus.Client;
using Namotion.Interceptor.Registry;

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
    IPrivateContextConfigurator,
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

    /// <summary>
    /// The maximum <see cref="PollingInterval"/> in seconds; longer intervals are lowered to it.
    /// </summary>
    public const int MaximumPollingIntervalSeconds = 3600;

    private const int UnknownFunctionMask = -1;

    // Decoupled from the polling interval so a (re)connect or a lost connection shows within a second.
    private static readonly TimeSpan StatusRefreshInterval = TimeSpan.FromSeconds(1);

    private readonly ILogger<LuxtronikHeatPump> _logger;
    private readonly SemaphoreSlim _configurationChanged = new(0, 1);

    // Written by the discovery on the source's thread, read by the status loop.
    private int _discoveredFunctionMask = UnknownFunctionMask;

    /// <summary>
    /// The source attachment, or null while none is wanted. Touched only by this subject's own start and run
    /// loop, which the hosting handler serializes. Kept across a stop: the attachment survives the subject
    /// leaving the graph, and the handler re-creates its source on re-entry, so a restarted run loop must
    /// reuse it rather than attach a second source beside that one.
    /// </summary>
    private IHostedServiceAttachment<ModbusSubjectClientSource>? _attachment;

    /// <summary>
    /// The fault the attachment carried out of the graph. It stays recorded until the handler's restart on re-entry
    /// clears it, and until then it describes the source before the stop rather than the one being started.
    /// </summary>
    private Exception? _faultBeforeStart;

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
    /// Gets or sets the interval between reads of the controller. Values below <see cref="MinimumPollingIntervalSeconds"/> are
    /// raised to it, values above <see cref="MaximumPollingIntervalSeconds"/> lowered to it.
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

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Attached here rather than from the run loop: the base start only schedules the loop, so an attach
        // issued there defers startup completion after this subject's own completion deferral is released,
        // and a startup completion wait could pass in between against a tree whose source is not attached yet.
        _faultBeforeStart = _attachment?.Fault;
        if (_attachment is null && !string.IsNullOrWhiteSpace(HostAddress) && TryValidateConfiguration(out _))
        {
            _attachment = AttachSource();
        }

        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Deliberately does not detach: this runs inside the handler's stop transition for this subject, and the
        // source's stop is ordered behind it, so a detach awaited here would wait on itself. The handler stops and
        // disposes the source once this returns. See docs/hosting.md#do-not-detach-from-your-own-stop-path.
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // Here rather than at the tail of the run loop: a stop that lands before the base start has scheduled the
        // loop cancels it without running it.
        IsConnected = false;
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var hostAddress = HostAddress;
            if (string.IsNullOrWhiteSpace(hostAddress))
            {
                if (_attachment is { } attachment)
                {
                    // Re-created by the handler on re-entry, from an address cleared while the subject was out of the graph.
                    await DetachSourceAsync(attachment, stoppingToken).ConfigureAwait(false);
                }

                Status = ServiceStatus.Stopped;
                StatusMessage = "No host address configured";
                await WaitForConfigurationChangeAsync(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
                continue;
            }

            await RunSourceAsync(hostAddress, stoppingToken).ConfigureAwait(false);
        }
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
    /// Gets the configured <see cref="PollingInterval"/>, raised to <see cref="MinimumPollingInterval"/> and lowered to
    /// <see cref="MaximumPollingIntervalSeconds"/>.
    /// </summary>
    internal TimeSpan GetEffectivePollingInterval()
    {
        var maximum = TimeSpan.FromSeconds(MaximumPollingIntervalSeconds);
        return TimeSpan.FromTicks(Math.Clamp(PollingInterval.Ticks, MinimumPollingInterval.Ticks, maximum.Ticks));
    }

    /// <summary>
    /// Runs the attached source until a configuration edit, a function change or a failure asks for a new one, then
    /// detaches it so that the next one never polls beside it. Returns without detaching when the subject stops.
    /// </summary>
    private async Task RunSourceAsync(string hostAddress, CancellationToken stoppingToken)
    {
        // Captured once, so a configuration edit applies only through the restart it signals.
        var pollingInterval = GetEffectivePollingInterval();

        if (_attachment is null && !TryValidateConfiguration(out var configurationError))
        {
            _logger.LogError(configurationError, "Luxtronik heat pump {HostAddress} has an invalid configuration.", hostAddress);
            Status = ServiceStatus.Error;
            StatusMessage = configurationError.Message;
            await WaitForConfigurationChangeAsync(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
            return;
        }

        var attachment = _attachment ??= AttachSource();

        Exception? failure = null;
        while (!stoppingToken.IsCancellationRequested && TryMirrorAttachment(attachment, out failure))
        {
            if (HaveFunctionsChanged(hostAddress) ||
                await WaitForConfigurationChangeAsync(StatusRefreshInterval, stoppingToken).ConfigureAwait(false))
            {
                break;
            }
        }

        if (stoppingToken.IsCancellationRequested)
        {
            // The handler stops and disposes the source behind this subject's own stop, which this unwind is part of.
            return;
        }

        await DetachSourceAsync(attachment, stoppingToken).ConfigureAwait(false);

        if (failure is not null)
        {
            _logger.LogError(failure, "Luxtronik heat pump {HostAddress} failed.", hostAddress);
            Status = ServiceStatus.Error;
            StatusMessage = failure.Message;
            await WaitForConfigurationChangeAsync(pollingInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Mirrors what the attachment holds into this device's state, and returns false with the fault once the handler
    /// has recorded one that no start is retrying.
    /// </summary>
    private bool TryMirrorAttachment(IHostedServiceAttachment<ModbusSubjectClientSource> attachment, out Exception? failure)
    {
        // The fault first and the state after it: docs/hosting.md#reading-the-outcome.
        var fault = attachment.Fault;
        var state = attachment.GetState(out var source);
        if (source is not null)
        {
            // Source diagnostics are not tracked properties, so they are mirrored into this device's state.
            UpdateStatus(source.Diagnostics);
        }
        else if (fault is not null &&
                 state is not HostedServiceAttachmentState.Starting &&
                 !ReferenceEquals(fault, _faultBeforeStart))
        {
            failure = fault;
            return false;
        }
        else
        {
            IsConnected = false;
            Status = ServiceStatus.Starting;
            StatusMessage = "Connecting...";
        }

        failure = null;
        return true;
    }

    private IHostedServiceAttachment<ModbusSubjectClientSource> AttachSource()
    {
        // Discards the previous source's discovery, whose mismatch with the polled flags would restart this source again.
        Volatile.Write(ref _discoveredFunctionMask, UnknownFunctionMask);
        return this.AttachHostedService(CreateSource);
    }

    /// <summary>
    /// Builds the source for the attachment. Reads the configuration when invoked rather than capturing it at attach
    /// time, because the handler invokes it again when the subject re-enters the graph.
    /// </summary>
    private ModbusSubjectClientSource CreateSource()
    {
        return this.CreateModbusClientSource(CreateConfiguration(), _logger);
    }

    private ModbusClientConfiguration CreateConfiguration()
    {
        return new ModbusClientConfiguration
        {
            Host = HostAddress ?? string.Empty,
            Port = Port,
            PollingInterval = GetEffectivePollingInterval()
        };
    }

    /// <summary>
    /// Validates the configuration before a source is attached, so that one only an edit can fix is reported at once
    /// rather than as a fault of the attachment.
    /// </summary>
    private bool TryValidateConfiguration([NotNullWhen(false)] out ArgumentException? error)
    {
        try
        {
            CreateConfiguration().Validate();
            error = null;
            return true;
        }
        catch (ArgumentException exception)
        {
            error = exception;
            return false;
        }
    }

    /// <summary>
    /// Gets whether the polled <see cref="Functions"/> differ, in a flag of <see cref="LuxtronikGating.DiscoveryFunctionMask"/>,
    /// from the flags the last discovery read, which excluded the registers of the inactive functions. <c>false</c> until
    /// both are known.
    /// </summary>
    private bool HaveFunctionsChanged(string hostAddress)
    {
        var discoveredMask = Volatile.Read(ref _discoveredFunctionMask);

        if (discoveredMask == UnknownFunctionMask || Functions.GetFunctionMask() is not { } polledMask)
        {
            return false;
        }

        // The final re-read drops a comparison against a mask a discovery replaced while the flags were read.
        var changedMask = (polledMask ^ discoveredMask) & LuxtronikGating.DiscoveryFunctionMask;
        if (changedMask == 0 || Volatile.Read(ref _discoveredFunctionMask) != discoveredMask)
        {
            return false;
        }

        _logger.LogInformation(
            "Luxtronik {HostAddress} changed its active functions ({ChangedFunctions}); discovering its registers again.",
            hostAddress, LuxtronikGating.GetFunctionNames(changedMask));
        return true;
    }

    /// <summary>
    /// Detaches the source and waits for the handler to stop and dispose it. Called from the run loop only, never
    /// from the unwind on <paramref name="stoppingToken"/>, which runs inside this subject's own stop.
    /// </summary>
    private async Task DetachSourceAsync(
        IHostedServiceAttachment<ModbusSubjectClientSource> attachment, CancellationToken stoppingToken)
    {
        // Cleared ahead of the call: the detach removes the attachment before its wait begins and the stop runs
        // whatever the token does, so the handle is spent on every path below. The stopping token rather than none: a
        // subject stop landing meanwhile orders this source's stop behind its own, and an unbounded wait here would
        // then wait on itself. A cancelled token also cuts the source's own stop short; its dispose still waits for it.
        _attachment = null;
        try
        {
            await this.DetachHostedServiceAsync(attachment, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The handler finishes the stop on its own.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to detach the Modbus source of {HeatPump}.", Title);
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

    void IPrivateContextConfigurator.ConfigureContext(IInterceptorSubjectContext context)
    {
        // The discovery and the register resolver read the registry, so running alone needs one too.
        context.WithRegistry();
    }

    public override void Dispose()
    {
        base.Dispose();
        _configurationChanged.Dispose();
    }
}
