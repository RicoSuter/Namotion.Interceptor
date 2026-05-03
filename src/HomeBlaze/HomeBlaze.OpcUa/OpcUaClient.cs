using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.OpcUa;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.OpcUa;

/// <summary>
/// OPC UA client subject that connects to an OPC UA server and discovers its address space dynamically.
/// </summary>
[Category("Clients")]
[Description("Connects to an OPC UA server and discovers properties dynamically")]
[InterceptorSubject]
public partial class OpcUaClient : BackgroundService, IConfigurable, ITitleProvider, IIconProvider
{
    private readonly ILogger<OpcUaClient> _logger;
    private IOpcUaSubjectClientSource? _clientSource;

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
    /// Optional OPC UA root node name to start browsing from under the Objects folder.
    /// </summary>
    [Configuration]
    public partial string? RootName { get; set; }

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
    /// Error message when Status is Error.
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
    public partial double? IncomingChangesPerSecond { get; set; }

    /// <summary>
    /// Average outgoing changes per second (client to server). Null when not running.
    /// </summary>
    [State]
    public partial double? OutgoingChangesPerSecond { get; set; }

    // Operations

    /// <summary>
    /// Starts the OPC UA client and enables auto-start on next application startup.
    /// </summary>
    [Operation(Title = "Start", Position = 1, Icon = "Start", RequiresConfirmation = true)]
    public Task StartAsync()
    {
        IsEnabled = true;
        return StartClientAsync(CancellationToken.None);
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
        return StopClientAsync(CancellationToken.None);
    }

    [Derived]
    [PropertyAttribute("Stop", KnownAttributes.IsEnabled)]
    public bool Stop_IsEnabled => Status is ServiceStatus.Running or ServiceStatus.Starting;

    // Interface implementations

    public string? Title => Name;

    public string? IconName => "Cable";

    [Derived]
    public string? IconColor => Status == ServiceStatus.Running ? "Success" : null;

    public OpcUaClient(ILogger<OpcUaClient> logger)
    {
        _logger = logger;

        Name = string.Empty;
        ServerUrl = string.Empty;
        Status = ServiceStatus.Stopped;
        IsEnabled = true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (IsEnabled)
        {
            await StartClientAsync(stoppingToken);
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_clientSource is { } source)
                {
                    var diagnostics = source.Diagnostics;
                    IsConnected = diagnostics.IsConnected;
                    IncomingChangesPerSecond = diagnostics.IncomingChangesPerSecond;
                    OutgoingChangesPerSecond = diagnostics.OutgoingChangesPerSecond;
                }

                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        await StopClientAsync(CancellationToken.None);
    }

    public async Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        await StopClientAsync(cancellationToken);
        await StartClientAsync(cancellationToken);
    }

    private async Task StartClientAsync(CancellationToken cancellationToken)
    {
        try
        {
            Status = ServiceStatus.Starting;
            StatusMessage = null;

            if (string.IsNullOrEmpty(ServerUrl))
            {
                Status = ServiceStatus.Error;
                StatusMessage = "Server URL is not configured";
                return;
            }

            var configuration = new OpcUaClientConfiguration
            {
                ServerUrl = ServerUrl,
                RootName = RootName,
                TypeResolver = new OpcUaTypeResolver(_logger),
                ValueConverter = new OpcUaValueConverter(),
                SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),
            };

            _clientSource = this.CreateOpcUaClientSource(configuration, _logger);
            await this.AttachHostedServiceAsync(_clientSource, cancellationToken);

            Status = ServiceStatus.Running;
            _logger.LogInformation("OPC UA client started for server: {ServerUrl}", ServerUrl);
        }
        catch (Exception ex)
        {
            Status = ServiceStatus.Error;
            StatusMessage = ex.Message;
            _logger.LogError(ex, "Failed to start OPC UA client");
        }
    }

    private async Task StopClientAsync(CancellationToken cancellationToken)
    {
        if (_clientSource != null)
        {
            try
            {
                Status = ServiceStatus.Stopping;
                await this.DetachHostedServiceAsync(_clientSource, cancellationToken);
                _logger.LogInformation("OPC UA client stopped");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop OPC UA client");
            }
            finally
            {
                _clientSource = null;
                Status = ServiceStatus.Stopped;
                IsConnected = null;
                IncomingChangesPerSecond = null;
                OutgoingChangesPerSecond = null;
            }
        }
    }
}
