using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.OpcUa;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.OpcUa;

/// <summary>
/// OPC UA server subject that exposes other subjects via OPC UA protocol.
/// </summary>
[Category("Servers")]
[Description("Exposes subjects via OPC UA protocol")]
[InterceptorSubject]
public partial class OpcUaServer : BackgroundService, IConfigurable, ITitleProvider, IIconProvider, IServerSubject
{
    private const string WaitingForStartupMessage = "Waiting for startup to complete";

    private readonly RootManager _rootManager;
    private readonly SubjectPathResolver _pathResolver;
    private readonly ILogger<OpcUaServer> _logger;

    // Serializes creating and removing _serverService. Never held while waiting for startup: a stop must be
    // able to run meanwhile, also from a storage applying configuration under its own lock.
    private readonly SemaphoreSlim _serverLock = new(1, 1);

    // Cancelled and replaced by every stop, which ends the starts still waiting for startup. A replaced source
    // is never disposed: a start may still be linking to it.
    private CancellationTokenSource _startWaitCancellation = new();

    private IOpcUaSubjectServer? _serverService;

    /// <summary>
    /// How long a start waits for startup to complete before it builds the address space anyway.
    /// </summary>
    internal TimeSpan StartupWaitTimeout { get; set; } = TimeSpan.FromMinutes(5);

    // Configuration properties (persisted to JSON)

    /// <summary>
    /// Display name of the server.
    /// </summary>
    [Configuration]
    public partial string Name { get; set; }

    /// <summary>
    /// Subject path to expose via OPC UA (e.g., "/" or "/Children[demo]").
    /// </summary>
    [Configuration]
    public partial string Path { get; set; }

    /// <summary>
    /// OPC UA application name. Uses default if not specified.
    /// </summary>
    [Configuration]
    public partial string? ApplicationName { get; set; }

    /// <summary>
    /// OPC UA namespace URI. Uses default if not specified.
    /// </summary>
    [Configuration]
    public partial string? NamespaceUri { get; set; }

    /// <summary>
    /// OPC UA root folder name. Uses default if not specified.
    /// </summary>
    [Configuration]
    public partial string? RootName { get; set; }

    /// <summary>
    /// OPC UA server base address (e.g., "opc.tcp://localhost:4840/"). Uses default if not specified.
    /// </summary>
    [Configuration]
    public partial string? BaseAddress { get; set; }

    /// <summary>
    /// Whether to clean the certificate store on start. Uses default if not specified.
    /// </summary>
    [Configuration]
    public partial bool? CleanCertificateStore { get; set; }

    /// <summary>
    /// Change buffer time in milliseconds. Uses default if not specified.
    /// </summary>
    [Configuration]
    public partial int? BufferTimeMs { get; set; }

    /// <summary>
    /// Whether the server is enabled and should auto-start on application startup.
    /// When stopped manually, this is set to false to prevent auto-restart.
    /// </summary>
    [Configuration]
    [State(Position = 0)]
    public partial bool IsEnabled { get; set; }

    // State properties (runtime only)

    /// <summary>
    /// Current server status.
    /// </summary>
    [State]
    public partial ServiceStatus Status { get; set; }

    /// <summary>
    /// Error message when Status is Error.
    /// </summary>
    [State]
    public partial string? StatusMessage { get; set; }

    /// <summary>
    /// Average incoming changes per second (client writes to server). Null when not running.
    /// </summary>
    [State]
    public partial double? IncomingChangesPerSecond { get; set; }

    /// <summary>
    /// Average outgoing changes per second (subject changes pushed to OPC UA nodes). Null when not running.
    /// </summary>
    [State]
    public partial double? OutgoingChangesPerSecond { get; set; }

    /// <summary>
    /// Number of active OPC UA client sessions. Null when not running.
    /// </summary>
    [State]
    public partial int? ActiveSessionCount { get; set; }

    // Operations

    /// <summary>
    /// Starts the OPC UA server and enables auto-start on next application startup.
    /// </summary>
    [Operation(Title = "Start", Position = 1, Icon = "Start", RequiresConfirmation = true)]
    public Task StartAsync()
    {
        IsEnabled = true;
        return StartServerAsync(CancellationToken.None);
    }

    [Derived]
    [PropertyAttribute("Start", KnownAttributes.IsEnabled)]
    public bool Start_IsEnabled => Status == ServiceStatus.Stopped || Status == ServiceStatus.Error;

    /// <summary>
    /// Stops the OPC UA server and disables auto-start on next application startup.
    /// </summary>
    [Operation(Title = "Stop", Position = 2, Icon = "Stop", RequiresConfirmation = true)]
    public Task StopAsync()
    {
        IsEnabled = false;
        return StopServerAsync(CancellationToken.None);
    }

    [Derived]
    [PropertyAttribute("Stop", KnownAttributes.IsEnabled)]
    public bool Stop_IsEnabled => Status is ServiceStatus.Running or ServiceStatus.Starting; // TODO: Should check state of _serverService

    // Interface implementations

    [Derived]
    public bool IsServerRunning => Status == ServiceStatus.Running;

    public string? Title => Name;

    public string? IconName => "Dns";

    [Derived]
    public string? IconColor => Status == ServiceStatus.Running ? "Success" : null;

    public OpcUaServer(
        RootManager rootManager,
        SubjectPathResolver pathResolver,
        ILogger<OpcUaServer> logger)
    {
        _rootManager = rootManager;
        _pathResolver = pathResolver;
        _logger = logger;

        Name = string.Empty;
        Path = string.Empty;
        Status = ServiceStatus.Stopped;
        IsEnabled = true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (IsEnabled)
        {
            await StartServerAsync(stoppingToken);
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_serverService is { } service)
                {
                    var diagnostics = service.Diagnostics;
                    IncomingChangesPerSecond = diagnostics.Throughput.IncomingPerSecond;
                    OutgoingChangesPerSecond = diagnostics.Throughput.OutgoingPerSecond;
                    ActiveSessionCount = diagnostics.ActiveSessionCount;
                }

                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        await StopServerAsync(CancellationToken.None);
    }

    public async Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        await StopServerAsync(cancellationToken);
        if (!IsEnabled)
        {
            return;
        }

        if (((IInterceptorSubject)this).Context.IsStartupCompleted())
        {
            await StartServerAsync(cancellationToken);
        }
        else
        {
            // Storages apply configuration under their lock, which a pending placeholder upgrade needs before
            // startup can complete, so waiting for startup here could deadlock. The stop above also ended the
            // start ExecuteAsync was waiting with, so this one takes its place.
            _ = StartServerAsync(CancellationToken.None);
        }
    }

    private async Task StartServerAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!await WaitForStartupAsync(cancellationToken))
            {
                return;
            }

            await _serverLock.WaitAsync(cancellationToken);
            try
            {
                if (_serverService is not null)
                {
                    // A concurrent start won; it may have run while this one reported waiting.
                    Status = ServiceStatus.Running;
                    StatusMessage = null;
                    return;
                }

                if (!IsEnabled)
                {
                    Status = ServiceStatus.Stopped;
                    StatusMessage = null;
                    return;
                }

                await CreateServerAsync(cancellationToken);
            }
            finally
            {
                _serverLock.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = ServiceStatus.Stopped;
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            Status = ServiceStatus.Error;
            StatusMessage = ex.Message;
            _logger.LogError(ex, "Failed to start OPC UA server");
        }
    }

    /// <summary>
    /// Waits until the root is loaded and startup completed, or the timeout passed. Returns false when a stop or
    /// <paramref name="cancellationToken"/> ended the wait.
    /// </summary>
    private async Task<bool> WaitForStartupAsync(CancellationToken cancellationToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, Volatile.Read(ref _startWaitCancellation).Token);

        if (_serverService is null)
        {
            Status = ServiceStatus.Starting;
            StatusMessage = WaitingForStartupMessage;
        }

        try
        {
            // WaitAsync does not observe the token when the task is already complete, so the
            // cancellation has to be checked in its own right.
            waitCancellation.Token.ThrowIfCancellationRequested();
            await _rootManager.RootLoaded.WaitAsync(waitCancellation.Token);

            // The address space is built once and does not follow subjects attached later, so wait until
            // plugin providers loaded their types and storages replaced their placeholders.
            try
            {
                await ((IInterceptorSubject)this).Context
                    .WaitForStartupAsync(waitCancellation.Token)
                    .WaitAsync(StartupWaitTimeout, waitCancellation.Token);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning(
                    "Startup did not complete within {Timeout}, so the OPC UA server for {Path} starts anyway. " +
                    "Subjects of plugins that are still loading may be missing; stop and start the server to rebuild its address space.",
                    StartupWaitTimeout, Path);
            }

            return true;
        }
        catch (OperationCanceledException) when (waitCancellation.IsCancellationRequested || _rootManager.RootLoaded.IsCanceled)
        {
            await _serverLock.WaitAsync(CancellationToken.None);
            try
            {
                if (_serverService is null)
                {
                    Status = ServiceStatus.Stopped;
                    StatusMessage = null;
                }
            }
            finally
            {
                _serverLock.Release();
            }

            return false;
        }
    }

    // Callers hold _serverLock.
    private async Task CreateServerAsync(CancellationToken cancellationToken)
    {
        Status = ServiceStatus.Starting;
        StatusMessage = null;

        if (string.IsNullOrEmpty(Path))
        {
            Status = ServiceStatus.Error;
            StatusMessage = "Path is not configured";
            return;
        }

        // Resolve the target subject from path
        var targetSubject = _pathResolver.ResolveSubject(Path, PathStyle.Canonical);
        if (targetSubject == null)
        {
            Status = ServiceStatus.Error;
            StatusMessage = $"Could not resolve subject at path: {Path}";
            return;
        }

        // Build configuration with defaults
        var defaults = new OpcUaServerConfiguration
        {
            ValueConverter = new OpcUaValueConverter()
        };

        var configuration = new OpcUaServerConfiguration
        {
            ValueConverter = new OpcUaValueConverter(),
            Mapper = new OpcUaCompositeMapper(
                new OpcUaPathProviderMapper(new StateAttributeOpcUaPathProvider()),
                new OpcUaAttributeMapper()),
            ApplicationName = ApplicationName ?? defaults.ApplicationName,
            NamespaceUri = NamespaceUri ?? defaults.NamespaceUri,
            RootName = RootName,
            BaseAddress = BaseAddress ?? defaults.BaseAddress,
            CleanCertificateStore = CleanCertificateStore ?? defaults.CleanCertificateStore,
            BufferTime = BufferTimeMs.HasValue ? TimeSpan.FromMilliseconds(BufferTimeMs.Value) : defaults.BufferTime,
        };

        // Separate from the client store: the server cleans its own store on start.
        if (OpcUaCertificateStoreLocation.Resolve(this, "Server") is { } certificateStorePath)
        {
            configuration.CertificateStoreBasePath = certificateStorePath;
        }

        var serverService = targetSubject.CreateOpcUaServer(configuration, _logger);
        try
        {
            await this.AttachHostedServiceAsync(serverService, cancellationToken);
        }
        catch
        {
            // A failed or cancelled start can leave the service registered with the host, possibly still starting.
            try
            {
                await this.DetachHostedServiceAsync(serverService, CancellationToken.None);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to detach the OPC UA server that did not start");
            }

            throw;
        }

        _serverService = serverService;

        Status = ServiceStatus.Running;
        _logger.LogInformation("OPC UA server started for path: {Path}", Path);
    }

    private async Task StopServerAsync(CancellationToken cancellationToken)
    {
        await Interlocked.Exchange(ref _startWaitCancellation, new CancellationTokenSource()).CancelAsync();

        await _serverLock.WaitAsync(cancellationToken);
        try
        {
            await DetachServerAsync(cancellationToken);
        }
        finally
        {
            _serverLock.Release();
        }
    }

    // Callers hold _serverLock.
    private async Task DetachServerAsync(CancellationToken cancellationToken)
    {
        if (_serverService != null)
        {
            try
            {
                Status = ServiceStatus.Stopping;
                await this.DetachHostedServiceAsync(_serverService, cancellationToken);
                _logger.LogInformation("OPC UA server stopped");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop OPC UA server");
            }
            finally
            {
                _serverService = null;
                Status = ServiceStatus.Stopped;
                IncomingChangesPerSecond = null;
                OutgoingChangesPerSecond = null;
                ActiveSessionCount = null;
            }
        }
    }

}
