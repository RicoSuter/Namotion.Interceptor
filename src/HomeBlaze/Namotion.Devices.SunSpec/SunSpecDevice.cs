using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Networking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Devices.SunSpec.Discovery;
using Namotion.Devices.SunSpec.Models;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Modbus.Client;
using Namotion.Interceptor.Registry;

namespace Namotion.Devices.SunSpec;

/// <summary>
/// A SunSpec device (inverter, meter, storage) read over Modbus TCP. Discovers the model chain of every configured
/// unit ID on each connect, and again periodically while a configured unit is missing. Read only.
/// </summary>
[Category("Devices")]
[Description("SunSpec device (inverter, meter, storage) via Modbus TCP, read only")]
[InterceptorSubject]
[System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S1200", Justification = "A device root aggregates its function subjects and the capability interfaces it implements; splitting it would only spread the same dependencies across files.")]
public partial class SunSpecDevice :
    BackgroundService,
    IPrivateContextConfigurator,
    IModbusDiscovery,
    IConfigurable,
    IConnectionState,
    IMonitoredService,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    /// <summary>
    /// The minimum <see cref="PollingInterval"/> in seconds; shorter intervals are raised to it.
    /// </summary>
    public const int MinimumPollingIntervalSeconds = 1;

    /// <summary>
    /// The maximum <see cref="PollingInterval"/> in seconds; longer intervals are lowered to it.
    /// </summary>
    public const int MaximumPollingIntervalSeconds = 3600;

    // Bridges the ID and length registers between models and pad registers; a SunSpec map is contiguous.
    private const int MaximumRegisterGap = 2;

    private const string ConnectionRefusedMessage = "Connection refused: the device may accept only one Modbus TCP client";

    // Decoupled from the polling interval so a (re)connect or a lost connection shows within a second.
    private static readonly TimeSpan StatusRefreshInterval = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan MaximumRediscoveryInterval = TimeSpan.FromHours(1);

    // Gives a device that accepts a single client time to release the previous connection.
    private static readonly TimeSpan RediscoveryReconnectDelay = TimeSpan.FromSeconds(1);

    private readonly ILogger<SunSpecDevice> _logger;
    private readonly SemaphoreSlim _configurationChanged = new(0, 1);
    private readonly SunSpecDefinitionDirectory _definitionDirectory = new();

    // Set before a source starts; read by its discovery on the source's thread.
    private byte[] _unitIds = [];
    private SunSpecModelCatalog _catalog = SunSpecModelCatalog.Empty;
    private TimeSpan _initialRediscoveryInterval;
    private bool _isPlannedRediscovery;

    // Written by a discovery, read by the next one; reset on a configuration change.
    private HashSet<byte> _missingUnitIds = [];

    // Written by the discovery, read by the status loop: false while a discovery replaces model subjects, whose
    // polled model IDs would otherwise look like a chain change.
    private bool _isChainGuardArmed;

    // Written by the discovery, read by the status loop.
    private string? _discoveryStatusMessage;
    private bool _hasMissingUnits;
    private long _lastDiscoveryTimestamp;
    private long _rediscoveryIntervalTicks;

    /// <summary>
    /// The source attachment, or null while none is wanted. Touched only by this subject's own start and run loop,
    /// which the hosting handler serializes. Kept across a stop: the attachment survives the subject leaving the
    /// graph, and the handler re-creates its source on re-entry, so a restarted run loop must reuse it rather than
    /// attach a second source beside that one.
    /// </summary>
    private IHostedServiceAttachment<ModbusSubjectClientSource>? _attachment;

    /// <summary>
    /// The fault the attachment carried out of the graph. It stays recorded until the handler's restart on re-entry
    /// clears it, and until then it describes the source before the stop rather than the one being started.
    /// </summary>
    private Exception? _faultBeforeStart;

    /// <summary>
    /// Why a source stopped, which decides how the next one starts.
    /// </summary>
    internal enum SourceRestart
    {
        ConfigurationChanged,
        Failed,
        ChainChanged,
        Rediscovery
    }

    // Protects a device from a hand-edited configuration that would poll it continuously; only tests lower it.
    internal TimeSpan MinimumPollingInterval { get; init; } = TimeSpan.FromSeconds(MinimumPollingIntervalSeconds);

    /// <summary>
    /// Gets or sets the first interval between discoveries while a configured unit is missing; the effective polling
    /// interval applies when it is longer. Only tests lower it.
    /// </summary>
    internal TimeSpan MinimumRediscoveryInterval { get; set; } = TimeSpan.FromSeconds(60);

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
    /// Gets or sets the Modbus unit IDs to discover, each between 1 and 247.
    /// </summary>
    [Configuration]
    public partial int[] UnitIds { get; set; }

    /// <summary>
    /// Gets or sets the interval between reads, limited to <see cref="MinimumPollingIntervalSeconds"/> and
    /// <see cref="MaximumPollingIntervalSeconds"/>.
    /// </summary>
    [Configuration]
    public partial TimeSpan PollingInterval { get; set; }

    /// <summary>
    /// Gets or sets the folder with additional SunSpec model definitions (SunSpec JSON) for models without a built-in
    /// class. A relative path starts at the data directory; <c>null</c> means "SunSpec/Models" in the data directory.
    /// </summary>
    [Configuration]
    public partial string? ModelDefinitionsPath { get; set; }

    /// <summary>
    /// Gets or sets whether discovery logs the raw registers of every model as a JSON fixture.
    /// </summary>
    [Configuration]
    public partial bool IsRegisterDumpEnabled { get; set; }

    [State(IsDiscrete = true)]
    public partial bool IsConnected { get; internal set; }

    [State(IsDiscrete = true)]
    public partial ServiceStatus Status { get; internal set; }

    [State]
    public partial string? StatusMessage { get; internal set; }

    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    /// <summary>
    /// Gets the discovered units by unit ID.
    /// </summary>
    public partial Dictionary<int, SunSpecUnit> Units { get; internal set; }

    [Derived]
    public string Title => string.IsNullOrEmpty(Name) ? "SunSpec Device" : Name;

    public string IconName => "SolarPower";

    [Derived]
    public string? IconColor => IsConnected ? "Success" : null;

    public SunSpecDevice(ILogger<SunSpecDevice> logger)
    {
        _logger = logger;

        Name = string.Empty;
        HostAddress = null;
        Port = 502;
        UnitIds = [1];
        PollingInterval = TimeSpan.FromSeconds(10);
        ModelDefinitionsPath = null;
        IsRegisterDumpEnabled = false;

        IsConnected = false;
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
        LastUpdated = null;
        Units = new Dictionary<int, SunSpecUnit>();
    }

    /// <summary>
    /// Walks the model chain of every configured unit ID, creates or keeps the model subjects, and excludes registers
    /// beyond a model's reported length.
    /// </summary>
    public async Task DiscoverAsync(ModbusDiscoveryContext context, CancellationToken cancellationToken)
    {
        DiscoveryCount++;
        Volatile.Write(ref _isChainGuardArmed, false);

        var units = new Dictionary<int, SunSpecUnit>();
        foreach (var unitId in _unitIds)
        {
            var unit = Units.GetValueOrDefault(unitId) ?? new SunSpecUnit(unitId);
            if (await SunSpecDiscovery.DiscoverUnitAsync(unit, _missingUnitIds.Contains(unitId), context, _catalog, IsRegisterDumpEnabled, _logger, cancellationToken).ConfigureAwait(false))
            {
                units[unitId] = unit;
            }
        }

        var haveFoundUnitsChanged = !SunSpecDiscovery.HaveSameUnitIds(Units, units);
        if (!SunSpecDiscovery.HaveSameUnits(Units, units))
        {
            Units = units;
        }

        _missingUnitIds = _unitIds.Where(unitId => !units.ContainsKey(unitId)).ToHashSet();
        var rediscoveryInterval = GetNextRediscoveryInterval(
            TimeSpan.FromTicks(Volatile.Read(ref _rediscoveryIntervalTicks)), _initialRediscoveryInterval, _isPlannedRediscovery, haveFoundUnitsChanged);

        // A reconnect of the same source discovers again, which is not a planned rediscovery.
        _isPlannedRediscovery = false;

        Volatile.Write(ref _rediscoveryIntervalTicks, rediscoveryInterval.Ticks);
        Volatile.Write(ref _discoveryStatusMessage, SunSpecDiscovery.GetStatusMessage(_unitIds, units));
        Volatile.Write(ref _hasMissingUnits, _missingUnitIds.Count > 0);
        Volatile.Write(ref _lastDiscoveryTimestamp, Stopwatch.GetTimestamp());
        SunSpecDiscovery.CompleteModels(units.Values, context);
        Volatile.Write(ref _isChainGuardArmed, true);
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

    /// <summary>
    /// Validates the configured unit IDs: at least one, each between 1 and 247; repeated IDs are used once.
    /// </summary>
    internal static bool TryGetUnitIds(int[]? configured, out byte[] unitIds, out string? error)
    {
        unitIds = [];
        if (configured is null || configured.Length == 0)
        {
            error = "No unit IDs configured";
            return false;
        }

        if (configured.Any(unitId => unitId is < 1 or > 247))
        {
            error = "Unit IDs must be between 1 and 247";
            return false;
        }

        unitIds = configured.Distinct().Select(unitId => (byte)unitId).ToArray();
        error = null;
        return true;
    }

    /// <summary>
    /// Gets the folder of additional model definitions, or <c>null</c> when none is configured and there is no data directory.
    /// </summary>
    internal string? GetModelDefinitionsDirectory()
    {
        var dataDirectory = ((IInterceptorSubject)this).Context.TryGetService<IDataDirectoryProvider>()?.DataDirectory;
        var path = ModelDefinitionsPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return dataDirectory is null ? null : Path.Combine(dataDirectory, "SunSpec", "Models");
        }

        return dataDirectory is null ? Path.GetFullPath(path) : Path.GetFullPath(path, dataDirectory);
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
    /// Mirrors the source diagnostics into <see cref="IsConnected"/>, <see cref="Status"/>, <see cref="StatusMessage"/>
    /// and <see cref="LastUpdated"/>. Disconnected reads as <see cref="ServiceStatus.Error"/> once the source reported an
    /// error, which stays set while it reconnects.
    /// </summary>
    internal void UpdateStatus(ModbusClientDiagnostics diagnostics)
    {
        IsConnected = diagnostics.IsOperational == true;
        LastUpdated = diagnostics.Polling.LastPollTime ?? LastUpdated;

        if (IsConnected)
        {
            Status = ServiceStatus.Running;
            StatusMessage = Volatile.Read(ref _discoveryStatusMessage);
        }
        else if (diagnostics.LastError is { } lastError)
        {
            Status = ServiceStatus.Error;
            StatusMessage = IsConnectionRefused(lastError) ? ConnectionRefusedMessage : lastError.Message;
        }
        else
        {
            Status = ServiceStatus.Starting;
            StatusMessage = "Connecting...";
        }
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Attached here rather than from the run loop: the base start only schedules the loop, so an attach issued
        // there defers startup completion after this subject's own completion deferral is released, and a startup
        // completion wait could pass in between against a tree whose source is not attached yet.
        _faultBeforeStart = _attachment?.Fault;
        if (_attachment is null &&
            !string.IsNullOrWhiteSpace(HostAddress) &&
            TryPrepareSource(SourceRestart.ConfigurationChanged, out _))
        {
            _attachment = this.AttachHostedService(CreateSource);
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
        var restart = SourceRestart.ConfigurationChanged;
        while (!stoppingToken.IsCancellationRequested)
        {
            var hostAddress = HostAddress;
            if (string.IsNullOrWhiteSpace(hostAddress))
            {
                if (_attachment is { } attachment)
                {
                    // Re-created by the handler on re-entry, from an address cleared while the subject was out of the graph.
                    await DetachSourceAsync(attachment, isConnectionStateKept: false, stoppingToken).ConfigureAwait(false);
                }

                Status = ServiceStatus.Stopped;
                StatusMessage = "No host address configured";
                await WaitForConfigurationChangeAsync(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
                restart = SourceRestart.ConfigurationChanged;
                continue;
            }

            restart = await RunSourceAsync(hostAddress, restart, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs the attached source, or attaches one, until a configuration edit, a chain change, a rediscovery or a
    /// failure asks for a new one, then detaches it so that the next one never polls beside it. Returns without
    /// detaching when the subject stops.
    /// </summary>
    private async Task<SourceRestart> RunSourceAsync(string hostAddress, SourceRestart previousRestart, CancellationToken stoppingToken)
    {
        // Captured once, so a configuration edit applies only through the restart it signals.
        var pollingInterval = GetEffectivePollingInterval();
        var isPlannedRediscovery = previousRestart == SourceRestart.Rediscovery;
        if (_attachment is null)
        {
            if (!TryPrepareSource(previousRestart, out var configurationError))
            {
                _logger.LogError(configurationError, "SunSpec device {HostAddress} has an invalid configuration.", hostAddress);
                Status = ServiceStatus.Error;
                StatusMessage = configurationError.Message;
                await WaitForConfigurationChangeAsync(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
                return SourceRestart.ConfigurationChanged;
            }

            if (!isPlannedRediscovery)
            {
                Status = ServiceStatus.Starting;
                StatusMessage = "Connecting...";
            }

            _attachment = this.AttachHostedService(CreateSource);
        }

        var attachment = _attachment;
        var restart = await MonitorSourceAsync(attachment, hostAddress, isPlannedRediscovery, stoppingToken).ConfigureAwait(false);
        if (stoppingToken.IsCancellationRequested)
        {
            // The handler stops and disposes the source behind this subject's own stop, which this unwind is part of.
            return restart;
        }

        await DetachSourceAsync(attachment, isConnectionStateKept: restart == SourceRestart.Rediscovery, stoppingToken).ConfigureAwait(false);

        var reconnectDelay = GetReconnectDelay(restart, pollingInterval);
        if (reconnectDelay > TimeSpan.Zero &&
            await WaitForConfigurationChangeAsync(reconnectDelay, stoppingToken).ConfigureAwait(false))
        {
            restart = SourceRestart.ConfigurationChanged;
        }

        return restart;
    }

    /// <summary>
    /// Mirrors the attachment into this device's state until the source must restart or the configuration changes.
    /// </summary>
    private async Task<SourceRestart> MonitorSourceAsync(
        IHostedServiceAttachment<ModbusSubjectClientSource> attachment, string hostAddress, bool isPlannedRediscovery, CancellationToken stoppingToken)
    {
        // A planned rediscovery keeps the previous connection's state until this one is up or fails, so the device does
        // not appear to restart.
        var isKeepingStatus = isPlannedRediscovery;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (MirrorAttachment(attachment, hostAddress, ref isKeepingStatus) is { } restart)
            {
                return restart;
            }

            if (await WaitForConfigurationChangeAsync(StatusRefreshInterval, stoppingToken).ConfigureAwait(false))
            {
                break;
            }
        }

        return SourceRestart.ConfigurationChanged;
    }

    /// <summary>
    /// Mirrors what the attachment holds into this device's state, and returns why the source must restart, or null
    /// while it keeps running. Returns <see cref="SourceRestart.Failed"/> once the handler has recorded a fault that no
    /// start is retrying.
    /// </summary>
    private SourceRestart? MirrorAttachment(
        IHostedServiceAttachment<ModbusSubjectClientSource> attachment, string hostAddress, ref bool isKeepingStatus)
    {
        // The fault first and the state after it: docs/hosting.md#reading-the-outcome.
        var fault = attachment.Fault;
        var state = attachment.GetState(out var source);
        if (source is not null)
        {
            return MirrorSource(source.Diagnostics, hostAddress, ref isKeepingStatus);
        }

        if (fault is not null &&
            state is not HostedServiceAttachmentState.Starting &&
            !ReferenceEquals(fault, _faultBeforeStart))
        {
            _logger.LogError(fault, "SunSpec device {HostAddress} failed.", hostAddress);
            Status = ServiceStatus.Error;
            StatusMessage = fault.Message;
            return SourceRestart.Failed;
        }

        if (!isKeepingStatus)
        {
            IsConnected = false;
            Status = ServiceStatus.Starting;
            StatusMessage = "Connecting...";
        }

        return null;
    }

    /// <summary>
    /// Mirrors the source diagnostics, which are not tracked properties, and returns why the source must restart, or
    /// null while it keeps running.
    /// </summary>
    private SourceRestart? MirrorSource(ModbusClientDiagnostics diagnostics, string hostAddress, ref bool isKeepingStatus)
    {
        var isOperational = diagnostics.IsOperational == true;
        isKeepingStatus &= !isOperational && diagnostics.LastError is null;
        if (!isKeepingStatus)
        {
            UpdateStatus(diagnostics);
        }

        if (HasChainChanged(hostAddress))
        {
            return SourceRestart.ChainChanged;
        }

        return IsRediscoveryDue(hostAddress, isOperational) ? SourceRestart.Rediscovery : null;
    }

    /// <summary>
    /// Gets how long to wait before the next connect: one polling interval after a failure or a chain change, which
    /// may come from a device still rebuilding its chain, a short moment after a planned rediscovery, and none after a
    /// configuration change.
    /// </summary>
    internal static TimeSpan GetReconnectDelay(SourceRestart restart, TimeSpan pollingInterval) => restart switch
    {
        SourceRestart.Failed or SourceRestart.ChainChanged => pollingInterval,
        SourceRestart.Rediscovery => RediscoveryReconnectDelay,
        _ => TimeSpan.Zero
    };

    /// <summary>
    /// Gets the interval until the next rediscovery after a discovery: <paramref name="initial"/> when the found units
    /// changed, twice <paramref name="current"/> (at most an hour) after a planned rediscovery that found the same
    /// units, and <paramref name="current"/> otherwise.
    /// </summary>
    internal static TimeSpan GetNextRediscoveryInterval(TimeSpan current, TimeSpan initial, bool isPlannedRediscovery, bool haveFoundUnitsChanged)
    {
        if (haveFoundUnitsChanged)
        {
            return initial;
        }

        if (!isPlannedRediscovery)
        {
            return current;
        }

        var doubled = current * 2;
        return doubled < MaximumRediscoveryInterval ? doubled : MaximumRediscoveryInterval;
    }

    // A configuration change starts the rediscovery back-off over and reports missing units and chains again.
    private void ResetDiscoveryState(TimeSpan pollingInterval)
    {
        _initialRediscoveryInterval = pollingInterval > MinimumRediscoveryInterval ? pollingInterval : MinimumRediscoveryInterval;
        Volatile.Write(ref _rediscoveryIntervalTicks, _initialRediscoveryInterval.Ticks);
        _missingUnitIds = [];
        foreach (var unit in Units.Values)
        {
            unit.ChainDescription = null;
        }
    }

    /// <summary>
    /// Gets whether a configured unit is missing while connected and the rediscovery interval passed since the last
    /// discovery.
    /// </summary>
    private bool IsRediscoveryDue(string hostAddress, bool isOperational)
    {
        if (!isOperational || !Volatile.Read(ref _hasMissingUnits) ||
            Stopwatch.GetElapsedTime(Volatile.Read(ref _lastDiscoveryTimestamp)) < TimeSpan.FromTicks(Volatile.Read(ref _rediscoveryIntervalTicks)))
        {
            return false;
        }

        _logger.LogDebug("SunSpec device {HostAddress} misses configured units; discovering its units again.", hostAddress);
        return true;
    }

    /// <summary>
    /// Validates the configuration and prepares the discovery for the next source, so that a configuration only an edit
    /// can fix is reported at once rather than as a fault of the attachment.
    /// </summary>
    private bool TryPrepareSource(SourceRestart restart, [NotNullWhen(false)] out Exception? error)
    {
        if (!TryGetUnitIds(UnitIds, out var unitIds, out var unitIdError))
        {
            error = new ArgumentException(unitIdError);
            return false;
        }

        SunSpecModelCatalog catalog;
        try
        {
            catalog = new SunSpecModelCatalog(_definitionDirectory.Load(GetModelDefinitionsDirectory(), SunSpecModelFactory.IsGenerated, _logger));
            CreateConfiguration(unitIds).Validate();
        }
        catch (Exception exception)
        {
            error = exception;
            return false;
        }

        _unitIds = unitIds;
        _catalog = catalog;
        if (restart == SourceRestart.ConfigurationChanged)
        {
            ResetDiscoveryState(GetEffectivePollingInterval());
        }

        _isPlannedRediscovery = restart == SourceRestart.Rediscovery;

        // The models still hold the previous source's polls, so they are only compared again after this source's discovery.
        Volatile.Write(ref _isChainGuardArmed, false);
        error = null;
        return true;
    }

    /// <summary>
    /// Builds the source for the attachment. Reads the configuration when invoked rather than capturing it at attach
    /// time, because the handler invokes it again when the subject re-enters the graph.
    /// </summary>
    private ModbusSubjectClientSource CreateSource()
    {
        return this.CreateModbusClientSource(CreateConfiguration(_unitIds), _logger);
    }

    private ModbusClientConfiguration CreateConfiguration(byte[] unitIds)
    {
        return new ModbusClientConfiguration
        {
            Host = HostAddress ?? string.Empty,
            Port = Port,
            UnitId = unitIds[0],
            PollingInterval = GetEffectivePollingInterval(),
            MaximumRegisterGap = MaximumRegisterGap
        };
    }

    /// <summary>
    /// Gets whether a polled model ID differs from the model discovered at that address, which means the chain changed
    /// without a reconnect.
    /// </summary>
    private bool HasChainChanged(string hostAddress)
    {
        if (!Volatile.Read(ref _isChainGuardArmed))
        {
            return false;
        }

        foreach (var unit in Units.Values)
        {
            foreach (var model in unit.Devices.SelectMany(device => device.GetModels()))
            {
                if (model.ModelIdRegister is { } polledModelId && polledModelId != model.ModelId)
                {
                    _logger.LogWarning(
                        "SunSpec device {HostAddress} unit {UnitId} reports model {PolledModelId} at {Address} instead of {ModelId}; discovering its model chain again.",
                        hostAddress, unit.UnitId, polledModelId, model.BaseAddress, model.ModelId);
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsConnectionRefused(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Detaches the source and waits for the handler to stop and dispose it, which a device that accepts a single
    /// client needs before the next source connects. Called from the run loop only, never from the unwind on
    /// <paramref name="stoppingToken"/>, which runs inside this subject's own stop.
    /// </summary>
    private async Task DetachSourceAsync(
        IHostedServiceAttachment<ModbusSubjectClientSource> attachment, bool isConnectionStateKept, CancellationToken stoppingToken)
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
            _logger.LogWarning(exception, "Failed to detach the Modbus source of {Device}.", Title);
        }

        if (!isConnectionStateKept)
        {
            IsConnected = false;
        }
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
