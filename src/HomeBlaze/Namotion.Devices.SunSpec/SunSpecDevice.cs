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
[Category("Energy")]
[Description("SunSpec device (inverter, meter, storage) via Modbus TCP, read only")]
[DisplayName("SunSpec device")]
[SubjectIcon("SolarPower")]
[InterceptorSubject]
[System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S1200", Justification = "A device root aggregates its function subjects and the capability interfaces it implements; splitting it would only spread the same dependencies across files.")]
public partial class SunSpecDevice :
    BackgroundService,
    IPrivateContextConfigurator,
    IModbusDiscovery,
    IConfigurable,
    IConnectionState,
    INetworkHost,
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
    /// The running source, or null. This subject owns it for the duration of its own run: the start or the run loop
    /// starts it, and whichever of the run loop's restart and <see cref="StopAsync"/> takes it first stops and
    /// disposes it, so a loop that never ran or outlives a timed out stop still leaves nothing running.
    /// </summary>
    private ModbusSubjectClientSource? _source;

    /// <summary>Why the source the start tried to start failed, for the run loop to report and retry.</summary>
    private Exception? _startFailure;

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

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _startFailure = null;
        // Started here rather than from the run loop, which the base start only schedules: the source registers in its
        // own start, and inside this one that registration counts towards startup completion.
        if (!string.IsNullOrWhiteSpace(HostAddress) && TryPrepareSource(SourceRestart.ConfigurationChanged, out _))
        {
            _startFailure = await TryStartSourceAsync(cancellationToken).ConfigureAwait(false);
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // After the run loop has exited, and also for a loop that never ran because this stop landed first.
        await StopSourceAsync(isConnectionStateKept: false, cancellationToken).ConfigureAwait(false);

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
    /// Runs the source, or starts one, until a configuration edit, a chain change, a rediscovery or a failure asks for a
    /// new one, then stops it so that the next one never polls beside it. Returns without stopping it when the subject
    /// stops, whose own stop does.
    /// </summary>
    private async Task<SourceRestart> RunSourceAsync(string hostAddress, SourceRestart previousRestart, CancellationToken stoppingToken)
    {
        var isPlannedRediscovery = previousRestart == SourceRestart.Rediscovery;

        var failure = Interlocked.Exchange(ref _startFailure, null);
        if (failure is null && Volatile.Read(ref _source) is null)
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

            failure = await TryStartSourceAsync(stoppingToken).ConfigureAwait(false);
        }

        var restart = SourceRestart.ConfigurationChanged;
        if (failure is not null)
        {
            _logger.LogError(failure, "SunSpec device {HostAddress} failed.", hostAddress);
            Status = ServiceStatus.Error;
            StatusMessage = failure.Message;
            restart = SourceRestart.Failed;
        }
        else if (Volatile.Read(ref _source) is { } source)
        {
            restart = await MonitorSourceAsync(source, hostAddress, isPlannedRediscovery, stoppingToken).ConfigureAwait(false);
        }

        if (stoppingToken.IsCancellationRequested)
        {
            return restart;
        }

        // Awaited before the next source connects, which a device that accepts a single client needs.
        await StopSourceAsync(isConnectionStateKept: restart == SourceRestart.Rediscovery, stoppingToken).ConfigureAwait(false);

        var reconnectDelay = GetReconnectDelay(restart, GetEffectivePollingInterval());
        if (reconnectDelay > TimeSpan.Zero &&
            await WaitForConfigurationChangeAsync(reconnectDelay, stoppingToken).ConfigureAwait(false))
        {
            restart = SourceRestart.ConfigurationChanged;
        }

        return restart;
    }

    /// <summary>
    /// Mirrors the source into this device's state until the source must restart or the configuration changes.
    /// </summary>
    private async Task<SourceRestart> MonitorSourceAsync(
        ModbusSubjectClientSource source, string hostAddress, bool isPlannedRediscovery, CancellationToken stoppingToken)
    {
        // A planned rediscovery keeps the previous connection's state until this one is up or fails, so the device does
        // not appear to restart.
        var isKeepingStatus = isPlannedRediscovery;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (MirrorSource(source.Diagnostics, hostAddress, ref isKeepingStatus) is { } restart)
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

    /// <summary>Creates and starts a source, and returns why it failed, having stopped and disposed it, or null.</summary>
    private async Task<Exception?> TryStartSourceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var source = this.CreateModbusClientSource(CreateConfiguration(_unitIds), _logger);
            Volatile.Write(ref _source, source);
            await source.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await StopSourceAsync(isConnectionStateKept: false, CancellationToken.None).ConfigureAwait(false);
            return exception;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // Published after a stop that already took the previous source, which only a timed out stop leaves running.
            await StopSourceAsync(isConnectionStateKept: false, CancellationToken.None).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>
    /// Stops and disposes the source, if this caller takes it. A canceled token cuts the source's own stop short; the
    /// dispose still runs.
    /// </summary>
    private async Task StopSourceAsync(bool isConnectionStateKept, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _source, null) is not { } source)
        {
            return;
        }

        try
        {
            await source.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller's deadline, not a failure of the source.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to stop the Modbus source of {Device}.", Title);
        }

        try
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to dispose the Modbus source of {Device}.", Title);
        }

        if (!isConnectionStateKept)
        {
            IsConnected = false;
        }
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
    /// can fix is reported as such rather than as a failure that is retried.
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
