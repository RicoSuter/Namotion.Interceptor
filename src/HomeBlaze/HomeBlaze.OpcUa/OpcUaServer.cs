using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.OpcUa;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.OpcUa;

/// <summary>
/// OPC UA server subject that exposes other subjects via OPC UA protocol.
/// </summary>
/// <remarks>
/// Owns its server connector for the duration of its own run: the connector is started and stopped by
/// this subject's start, stop and configuration paths, never by the hosting graph.
/// </remarks>
[Category("Integrations")]
[Description("Exposes subjects via OPC UA protocol")]
[DisplayName("OPC UA server")]
[SubjectIcon("Dns")]
[InterceptorSubject]
public partial class OpcUaServer
    : BackgroundService, IConfigurable, ITitleProvider, IIconProvider, IServerSubject,
      IConnectorOwner<IOpcUaSubjectServer>
{
    private static readonly TimeSpan DefaultRootLoadWaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultDiagnosticsPollInterval = TimeSpan.FromSeconds(60);

    private readonly RootManager _rootManager;
    private readonly SubjectPathResolver _pathResolver;
    private readonly ILogger<OpcUaServer> _logger;
    private readonly TimeSpan _rootLoadWaitTimeout;

    /// <summary>The connector this wrapper owns for the duration of its own run.</summary>
    private readonly OwnedConnectorHost<IOpcUaSubjectServer> _connectorHost;

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
    [Category("Statistics")]
    public partial double? IncomingChangesPerSecond { get; set; }

    /// <summary>
    /// Average outgoing changes per second (subject changes pushed to OPC UA nodes). Null when not running.
    /// </summary>
    [State]
    [Category("Statistics")]
    public partial double? OutgoingChangesPerSecond { get; set; }

    /// <summary>
    /// Number of active OPC UA client sessions. Null when not running.
    /// </summary>
    [State]
    [Category("Statistics")]
    public partial int? ActiveSessionCount { get; set; }

    // Operations

    /// <summary>
    /// Starts the OPC UA server and enables auto-start on next application startup.
    /// </summary>
    [Operation(Title = "Start", Position = 1, Icon = "Start", RequiresConfirmation = true)]
    public Task StartAsync()
    {
        IsEnabled = true;
        return _connectorHost.StartAsync(CancellationToken.None);
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
        return _connectorHost.StopAsync(CancellationToken.None);
    }

    [Derived]
    [PropertyAttribute("Stop", KnownAttributes.IsEnabled)]
    public bool Stop_IsEnabled => Status is ServiceStatus.Running or ServiceStatus.Starting;

    // Interface implementations

    [Derived]
    public bool IsServerRunning => Status == ServiceStatus.Running;

    public string? Title => Name;

    public string? IconName => "Dns";

    [Derived]
    public string? IconColor => Status == ServiceStatus.Running ? "Success" : null;

    /// <remarks>
    /// <paramref name="diagnosticsPollInterval"/> is how often the running server's diagnostics are
    /// refreshed, and <paramref name="rootLoadWaitTimeout"/> how long a start waits for the root before
    /// it fails. Null takes the default for either.
    /// </remarks>
    public OpcUaServer(
        RootManager rootManager,
        SubjectPathResolver pathResolver,
        ILogger<OpcUaServer> logger,
        TimeSpan? diagnosticsPollInterval = null,
        TimeSpan? rootLoadWaitTimeout = null)
    {
        _rootManager = rootManager;
        _pathResolver = pathResolver;
        _logger = logger;
        _rootLoadWaitTimeout = rootLoadWaitTimeout ?? DefaultRootLoadWaitTimeout;
        _connectorHost = new OwnedConnectorHost<IOpcUaSubjectServer>(
            this, logger, diagnosticsPollInterval ?? DefaultDiagnosticsPollInterval);

        Name = string.Empty;
        Path = string.Empty;
        Status = ServiceStatus.Stopped;
        IsEnabled = true;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await _connectorHost.StartRunAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        return _connectorHost.StopRunAsync(base.StopAsync, cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return _connectorHost.RunAsync(stoppingToken);
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        return _connectorHost.ApplyConfigurationAsync(cancellationToken);
    }

    /// <summary>The running server connector, or null. Test only.</summary>
    internal IOpcUaSubjectServer? Connector => _connectorHost.Current;

    // Connector host callbacks

    string IConnectorOwner<IOpcUaSubjectServer>.LogName => "OPC UA server";

    string IConnectorOwner<IOpcUaSubjectServer>.LogTarget => Path;

    string? IConnectorOwner<IOpcUaSubjectServer>.GetConfigurationError()
    {
        return string.IsNullOrEmpty(Path) ? "Path is not configured" : null;
    }

    async ValueTask<bool> IConnectorOwner<IOpcUaSubjectServer>.WaitUntilStartableAsync(CancellationToken cancellationToken)
    {
        try
        {
            // WaitAsync does not observe the token when the task is already complete, so the caller's
            // cancellation has to be checked in its own right.
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            // Bounded because the start holds the host's gate across this wait and the Start operation
            // passes no token, so an unbounded wait on a root that never loads keeps Stop out.
            await _rootManager.RootLoaded.WaitAsync(_rootLoadWaitTimeout, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _rootManager.RootLoaded.IsCanceled)
        {
            return false;
        }
        catch (TimeoutException) when (!_rootManager.RootLoaded.IsFaulted)
        {
            // The root can finish loading between the timeout firing and this filter running.
            if (!_rootManager.RootLoaded.IsCompletedSuccessfully)
            {
                throw new InvalidOperationException(
                    $"The root manager did not load within {_rootLoadWaitTimeout.TotalSeconds:F0} seconds, so the path could not be resolved: {Path}");
            }

            return true;
        }
    }

    IOpcUaSubjectServer IConnectorOwner<IOpcUaSubjectServer>.CreateInstance()
    {
        // Only reached once the start has waited for the root, so the path resolves against the loaded
        // graph. Re-resolved on every start rather than captured: the graph may have replaced the subject
        // at that path since the previous one.
        var targetSubject = _pathResolver.ResolveSubject(Path, PathStyle.Canonical)
            ?? throw new InvalidOperationException($"Could not resolve subject at path: {Path}");

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

        return targetSubject.CreateOpcUaServer(configuration, _logger);
    }

    void IConnectorOwner<IOpcUaSubjectServer>.ApplyDiagnostics(IOpcUaSubjectServer server)
    {
        var diagnostics = server.Diagnostics;
        IncomingChangesPerSecond = diagnostics.Throughput.IncomingPerSecond;
        OutgoingChangesPerSecond = diagnostics.Throughput.OutgoingPerSecond;
        ActiveSessionCount = diagnostics.ActiveSessionCount;
    }

    void IConnectorOwner<IOpcUaSubjectServer>.ResetDiagnostics()
    {
        IncomingChangesPerSecond = null;
        OutgoingChangesPerSecond = null;
        ActiveSessionCount = null;
    }
}
