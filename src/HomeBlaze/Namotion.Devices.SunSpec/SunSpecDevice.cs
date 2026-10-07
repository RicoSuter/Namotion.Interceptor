using System.ComponentModel;
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

namespace Namotion.Devices.SunSpec;

/// <summary>
/// A SunSpec device (inverter, meter, storage) read over Modbus TCP. Discovers the model chain of every configured
/// unit ID on each connect. Read only.
/// </summary>
[Category("Devices")]
[Description("SunSpec device (inverter, meter, storage) via Modbus TCP, read only")]
[InterceptorSubject]
[System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S1200", Justification = "A device root aggregates its function subjects and the capability interfaces it implements; splitting it would only spread the same dependencies across files.")]
public partial class SunSpecDevice :
    BackgroundService,
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

    private readonly ILogger<SunSpecDevice> _logger;
    private readonly SemaphoreSlim _configurationChanged = new(0, 1);
    private readonly SunSpecDefinitionDirectory _definitionDirectory = new();

    // Set before a source starts; read by its discovery on the source's thread.
    private byte[] _unitIds = [];
    private SunSpecModelCatalog _catalog = SunSpecModelCatalog.Empty;

    // Written by the discovery, read by the status loop: false while a discovery replaces model subjects, whose
    // polled model IDs would otherwise look like a chain change.
    private bool _isChainGuardArmed;

    // Written by the discovery, read by the status loop.
    private string? _discoveryStatusMessage;

    // Protects a device from a hand-edited configuration that would poll it continuously; only tests lower it.
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

        var units = await SunSpecDiscovery.DiscoverAsync(Units, _unitIds, context, _catalog, IsRegisterDumpEnabled, _logger, cancellationToken).ConfigureAwait(false);
        if (!SunSpecDiscovery.HaveSameUnits(Units, units))
        {
            Units = units;
        }

        Volatile.Write(ref _discoveryStatusMessage, SunSpecDiscovery.GetStatusMessage(_unitIds, units));
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

    private async Task RunSourceAsync(string hostAddress, CancellationToken stoppingToken)
    {
        // Captured once, so a configuration edit applies only through the restart it signals.
        var pollingInterval = GetEffectivePollingInterval();
        var source = await TryCreateSourceAsync(hostAddress, pollingInterval, stoppingToken).ConfigureAwait(false);
        if (source is null)
        {
            return;
        }

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
                if (HasChainChanged(hostAddress) ||
                    await WaitForConfigurationChangeAsync(StatusRefreshInterval, stoppingToken).ConfigureAwait(false))
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
            _logger.LogError(exception, "SunSpec device {HostAddress} failed.", hostAddress);
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
    /// Prepares the discovery and creates the source, or reports an invalid configuration and waits for a change to it.
    /// </summary>
    private async Task<ModbusSubjectClientSource?> TryCreateSourceAsync(string hostAddress, TimeSpan pollingInterval, CancellationToken stoppingToken)
    {
        if (TryGetUnitIds(UnitIds, out var unitIds, out var error))
        {
            try
            {
                _unitIds = unitIds;
                _catalog = new SunSpecModelCatalog(_definitionDirectory.Load(GetModelDefinitionsDirectory(), SunSpecModelFactory.IsGenerated, _logger));

                // The models still hold the previous source's polls, so they are only compared again after this source's discovery.
                Volatile.Write(ref _isChainGuardArmed, false);

                return this.CreateModbusClientSource(
                    new ModbusClientConfiguration
                    {
                        Host = hostAddress,
                        Port = Port,
                        UnitId = unitIds[0],
                        PollingInterval = pollingInterval,
                        MaximumRegisterGap = MaximumRegisterGap
                    },
                    _logger);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "SunSpec device {HostAddress} has an invalid configuration.", hostAddress);
                error = exception.Message;
            }
        }

        Status = ServiceStatus.Error;
        StatusMessage = error;
        await WaitForConfigurationChangeAsync(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        return null;
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
