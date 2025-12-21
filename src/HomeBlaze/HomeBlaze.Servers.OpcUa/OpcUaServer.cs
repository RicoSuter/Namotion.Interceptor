using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.OpcUa;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.Servers.OpcUa;

/// <summary>
/// OPC UA server subject that exposes other subjects via OPC UA protocol.
/// </summary>
[Category("Servers")]
[Description("Exposes subjects via OPC UA protocol")]
[InterceptorSubject]
public partial class OpcUaServer : BackgroundService, IConfigurableSubject, ITitleProvider, IIconProvider, IServerSubject
{
    private readonly RootManager _rootManager;
    private readonly SubjectPathResolver _pathResolver;
    private readonly ILogger<OpcUaServer> _logger;
    private IHostedService? _serverService;

    // Configuration properties (persisted to JSON)

    /// <summary>
    /// Display name of the server.
    /// </summary>
    [Configuration]
    public partial string Name { get; set; }

    /// <summary>
    /// Subject path to expose via OPC UA (e.g., "Root" or "Root.Children[demo]").
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
    /// Retry delay time in seconds. Uses default if not specified.
    /// </summary>
    [Configuration]
    public partial int? RetryTimeSeconds { get; set; }

    /// <summary>
    /// Whether the server is enabled and should auto-start on application startup.
    /// When stopped manually, this is set to false to prevent auto-restart.
    /// </summary>
    [Configuration]
    [State]
    public partial bool IsEnabled { get; set; }

    // State properties (runtime only)

    /// <summary>
    /// Current server status.
    /// </summary>
    [State]
    public partial ServerStatus Status { get; set; }

    /// <summary>
    /// Error message when Status is Error.
    /// </summary>
    [State]
    public partial string? ErrorMessage { get; set; }

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
    public bool Start_IsEnabled => Status == ServerStatus.Stopped || Status == ServerStatus.Error;

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
    public bool Stop_IsEnabled => Status is ServerStatus.Running or ServerStatus.Starting; // TODO: Should check state of _serverService

    // Interface implementations

    public string? Title => Name;

    public string? Icon => "Dns";

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
        Status = ServerStatus.Stopped;
        IsEnabled = true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Only auto-start if enabled (respects last persisted state)
        if (IsEnabled)
        {
            await StartServerAsync(stoppingToken);
        }

        // Keep running until cancelled
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }

        await StopServerAsync(CancellationToken.None);
    }

    public async Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        await StopServerAsync(cancellationToken);
        await StartServerAsync(cancellationToken);
    }

    private async Task StartServerAsync(CancellationToken cancellationToken)
    {
        try
        {
            Status = ServerStatus.Starting;
            ErrorMessage = null;

            if (string.IsNullOrEmpty(Path))
            {
                Status = ServerStatus.Error;
                ErrorMessage = "Path is not configured";
                return;
            }

            // Wait for root to be loaded
            while (!_rootManager.IsLoaded && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(100, cancellationToken);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                Status = ServerStatus.Stopped;
                return;
            }

            // Resolve the target subject from path
            var targetSubject = Path == "Root"
                ? _rootManager.Root
                : _pathResolver.ResolveSubject(Path);
            if (targetSubject == null)
            {
                Status = ServerStatus.Error;
                ErrorMessage = $"Could not resolve subject at path: {Path}";
                return;
            }

            // Build configuration with defaults
            var pathProvider = DefaultPathProvider.Instance;
            var defaults = new OpcUaServerConfiguration
            {
                PathProvider = pathProvider,
                ValueConverter = new OpcUaValueConverter()
            };

            var configuration = new OpcUaServerConfiguration
            {
                PathProvider = pathProvider,
                ValueConverter = new OpcUaValueConverter(),
                ApplicationName = ApplicationName ?? defaults.ApplicationName,
                NamespaceUri = NamespaceUri ?? defaults.NamespaceUri,
                RootName = RootName,
                BaseAddress = BaseAddress ?? defaults.BaseAddress,
                CleanCertificateStore = CleanCertificateStore ?? defaults.CleanCertificateStore,
                BufferTime = BufferTimeMs.HasValue ? TimeSpan.FromMilliseconds(BufferTimeMs.Value) : defaults.BufferTime,
                RetryTime = RetryTimeSeconds.HasValue ? TimeSpan.FromSeconds(RetryTimeSeconds.Value) : defaults.RetryTime
            };

            _serverService = targetSubject.CreateOpcUaServer(configuration, _logger);
            await this.AttachHostedServiceAsync(_serverService, cancellationToken);

            Status = ServerStatus.Running;
            _logger.LogInformation("OPC UA server started for path: {Path}", Path);
        }
        catch (Exception ex)
        {
            Status = ServerStatus.Error;
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "Failed to start OPC UA server");
        }
    }

    private async Task StopServerAsync(CancellationToken cancellationToken)
    {
        if (_serverService != null)
        {
            try
            {
                Status = ServerStatus.Stopping;
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
                Status = ServerStatus.Stopped;
            }
        }
    }

}
