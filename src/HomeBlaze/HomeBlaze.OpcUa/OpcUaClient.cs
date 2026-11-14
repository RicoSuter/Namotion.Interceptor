using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.OpcUa;
using Namotion.Interceptor.OpcUa.Client;
using System.Text;
using Namotion.Interceptor.Registry.Attributes;
using Opc.Ua;

namespace HomeBlaze.OpcUa;

/// <summary>
/// OPC UA client subject that connects to an OPC UA server and discovers its address space dynamically.
/// </summary>
/// <remarks>
/// Owns its client source for the duration of its own run: the source is started and stopped by this
/// subject's start, stop and configuration paths, never by the hosting graph.
/// </remarks>
[Category("Integrations")]
[Description("Connects to an OPC UA server and discovers properties dynamically")]
[DisplayName("OPC UA client")]
[SubjectIcon("Cable")]
[InterceptorSubject]
public partial class OpcUaClient
    : BackgroundService, IConfigurable, ITitleProvider, IIconProvider, IConnectorOwner<IOpcUaSubjectClientSource>
{
    private readonly ILogger<OpcUaClient> _logger;

    /// <summary>The connector this wrapper owns for the duration of its own run.</summary>
    private readonly OwnedConnectorHost<IOpcUaSubjectClientSource> _connectorHost;

    // Configuration properties

    /// <summary>
    /// Display name of the client.
    /// </summary>
    [Configuration]
    public partial string Name { get; set; }

    /// <summary>
    /// OPC UA server endpoint URL (e.g., "opc.tcp://localhost:4840").
    /// </summary>
    [Configuration]
    public partial string ServerUrl { get; set; }

    /// <summary>
    /// Optional root path to start browsing from under the Objects folder (use / as delimiter, e.g. "Machines/MyMachine").
    /// </summary>
    [Configuration]
    public partial string? RootPath { get; set; }

    /// <summary>
    /// Optional username for OPC UA server authentication. When empty, anonymous authentication is used.
    /// </summary>
    [Configuration]
    public partial string? Username { get; set; }

    /// <summary>
    /// Optional password for OPC UA server authentication.
    /// </summary>
    [Configuration(IsSecret = true)]
    public partial string? Password { get; set; }

    /// <summary>
    /// Whether server certificates that are not in this client's trusted certificate store are accepted. Leave it
    /// off and trust the server's certificate instead, unless the network is trusted. A rejected certificate is
    /// written to the client's rejected store and shown in the status message.
    /// </summary>
    [Configuration]
    public partial bool AcceptUntrustedServerCertificates { get; set; }

    /// <summary>
    /// Default sampling interval in milliseconds for monitored items.
    /// Null uses the server default. 0 enables exception-based monitoring (immediate reporting).
    /// </summary>
    [Configuration]
    public partial int? SamplingInterval { get; set; }

    /// <summary>
    /// Whether the client is enabled and should auto-start on application startup.
    /// </summary>
    [Configuration]
    [State(Position = 0)]
    public partial bool IsEnabled { get; set; }

    // State properties

    /// <summary>
    /// Current client status.
    /// </summary>
    [State]
    public partial ServiceStatus Status { get; set; }

    /// <summary>
    /// Error message when Status is Error, or the last connection error while the client is running but not connected.
    /// </summary>
    [State]
    public partial string? StatusMessage { get; set; }

    /// <summary>
    /// Whether the client is currently connected. Null when not running.
    /// </summary>
    [State]
    public partial bool? IsConnected { get; set; }

    /// <summary>
    /// Average incoming changes per second (server to client). Null when not running.
    /// </summary>
    [State]
    [Category("Statistics")]
    public partial double? IncomingChangesPerSecond { get; set; }

    /// <summary>
    /// Average outgoing changes per second (client to server). Null when not running.
    /// </summary>
    [State]
    [Category("Statistics")]
    public partial double? OutgoingChangesPerSecond { get; set; }

    /// <summary>
    /// Number of monitored items in the client. Null when not running.
    /// </summary>
    [State]
    [Category("Statistics")]
    public partial double? MonitoredItemCount { get; set; }

    /// <summary>
    /// Number of items using polling fallback. Null when not running.
    /// </summary>
    [State]
    [Category("Statistics")]
    public partial int? PollingItemCount { get; set; }

    /// <summary>
    /// Number of writes queued for retry during disconnection. Null when not running.
    /// </summary>
    [State]
    [Category("Statistics")]
    public partial int? PendingWriteCount { get; set; }

    /// <summary>
    /// Total number of reconnections since start. Null when not running.
    /// </summary>
    [State(IsCumulative = true)]
    [Category("Statistics")]
    public partial long? TotalReconnections { get; set; }

    /// <summary>
    /// Dynamic root subject containing discovered OPC UA properties.
    /// Recreated on each connection to provide a clean slate.
    /// </summary>
    [State]
    public partial DynamicSubject? Root { get; set; }

    // Operations

    /// <summary>
    /// Starts the OPC UA client and enables auto-start on next application startup.
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
    /// Stops the OPC UA client and disables auto-start on next application startup.
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

    public string? Title => Name;

    public string? IconName => "Cable";

    [Derived]
    public string? IconColor => Status == ServiceStatus.Running && IsConnected == true ? "Success" : null;

    /// <remarks>
    /// <paramref name="diagnosticsPollInterval"/> is how often the running client's diagnostics are
    /// refreshed, and null takes the default.
    /// </remarks>
    public OpcUaClient(ILogger<OpcUaClient> logger, TimeSpan? diagnosticsPollInterval = null)
    {
        _logger = logger;
        _connectorHost = new OwnedConnectorHost<IOpcUaSubjectClientSource>(this, logger, diagnosticsPollInterval);

        Name = string.Empty;
        ServerUrl = string.Empty;
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

    /// <summary>The running client source, or null. Test only.</summary>
    internal IOpcUaSubjectClientSource? Connector => _connectorHost.Current;

    // Connector host callbacks

    string IConnectorOwner<IOpcUaSubjectClientSource>.LogName => "OPC UA client";

    string IConnectorOwner<IOpcUaSubjectClientSource>.LogTarget => ServerUrl;

    string? IConnectorOwner<IOpcUaSubjectClientSource>.GetConfigurationError()
    {
        return string.IsNullOrEmpty(ServerUrl) ? "Server URL is not configured" : null;
    }

    IOpcUaSubjectClientSource IConnectorOwner<IOpcUaSubjectClientSource>.CreateInstance()
    {
        var rootPathSegments = RootPath?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var root = new OpcUaDynamicSubject(rootPathSegments is { Length: > 0 } ? rootPathSegments[^1] : "Root");
        Root = root;

        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = ServerUrl,
            RootPath = rootPathSegments,
            DefaultSamplingInterval = SamplingInterval,
            TypeResolver = new HomeBlazeOpcUaTypeResolver(_logger),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new HomeBlazeOpcUaSubjectFactory(),
            CreateUserIdentity = !string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(Password)
                ? _ => Task.FromResult(new UserIdentity(Username, Encoding.UTF8.GetBytes(Password)))
                : null,
            AutoAcceptUntrustedCertificates = AcceptUntrustedServerCertificates,
        };

        if (OpcUaCertificateStoreLocation.Resolve(this, "Client") is { } certificateStorePath)
        {
            configuration.CertificateStoreBasePath = certificateStorePath;
        }

        return root.CreateOpcUaClientSource(configuration, _logger);
    }

    void IConnectorOwner<IOpcUaSubjectClientSource>.ApplyDiagnostics(IOpcUaSubjectClientSource source)
    {
        var diagnostics = source.Diagnostics;
        IsConnected = diagnostics.IsOperational;

        // Without this, a rejected server certificate leaves the client looking healthy while it never connects.
        if (Status == ServiceStatus.Running && diagnostics.IsOperational is { } isOperational)
        {
            StatusMessage = isOperational ? null : diagnostics.LastError?.Message;
        }

        IncomingChangesPerSecond = diagnostics.Throughput.IncomingPerSecond;
        OutgoingChangesPerSecond = diagnostics.Throughput.OutgoingPerSecond;
        MonitoredItemCount = diagnostics.MonitoredItemCount;
        PollingItemCount = diagnostics.Polling?.ItemCount ?? 0;
        PendingWriteCount = diagnostics.OutboundRetries.Depth;
        TotalReconnections = diagnostics.Reconnects.TotalAttempts;
    }

    void IConnectorOwner<IOpcUaSubjectClientSource>.ResetDiagnostics()
    {
        IsConnected = null;
        IncomingChangesPerSecond = null;
        OutgoingChangesPerSecond = null;
        MonitoredItemCount = null;
        PollingItemCount = null;
        PendingWriteCount = null;
        TotalReconnections = null;
    }

    /// <summary>
    /// Drops the discovered tree, which belongs to the source that filled it and is built again by the
    /// next start.
    /// </summary>
    void IConnectorOwner<IOpcUaSubjectClientSource>.DropInstanceState()
    {
        Root = null;
    }
}
