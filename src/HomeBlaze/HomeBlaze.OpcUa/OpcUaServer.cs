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
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RootLoadPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly RootManager _rootManager;
    private readonly SubjectPathResolver _pathResolver;
    private readonly ILogger<OpcUaServer> _logger;

    /// <summary>
    /// The single attachment this wrapper owns, or null when nothing is attached. Two servers bound to
    /// the same subject would race over the same endpoint, so the start path attaches only when this is
    /// null.
    /// </summary>
    private IHostedServiceAttachment<IOpcUaSubjectServer>? _attachment;

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
    public bool Stop_IsEnabled => Status is ServiceStatus.Running or ServiceStatus.Starting;

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
                UpdateFromAttachment();
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        // Deliberately does NOT detach. ExecuteAsync unwinds inside the handler's own stop transition for
        // this subject, and detaching from there waits on the attachment chain, whose head is waiting on
        // this subject's stop to complete. The handler owns the detach on graph events; the explicit
        // detach lives on the Stop operation and ApplyConfigurationAsync, neither of which is reached
        // through StopAsync.
        Status = ServiceStatus.Stopped;
        ResetDiagnostics();
    }

    public async Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        await StopServerAsync(cancellationToken);

        // Guarded, unlike ExecuteAsync's caller-side check alone: an edit that disables the server used
        // to stop it and start it again in the same call.
        if (IsEnabled)
        {
            await StartServerAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Reconciles the reported status and the diagnostics with what the attachment actually holds. The
    /// handler creates, faults and disposes the instance on its own chain (a context re-attach re-invokes
    /// the factory without going through this wrapper), so polling the handle is the only way those
    /// outcomes reach the UI.
    /// </summary>
    private void UpdateFromAttachment()
    {
        if (_attachment is not { } attachment || Status is ServiceStatus.Stopping or ServiceStatus.Stopped)
        {
            return;
        }

        if (attachment.Fault is { } fault)
        {
            Status = ServiceStatus.Error;
            StatusMessage = fault.Message;
            ResetDiagnostics();
            return;
        }

        if (attachment.Current is not { } server)
        {
            // Attached but not yet created: the handler's start transition has not run.
            Status = ServiceStatus.Starting;
            return;
        }

        Status = ServiceStatus.Running;

        var diagnostics = server.Diagnostics;
        IncomingChangesPerSecond = diagnostics.IncomingChangesPerSecond;
        OutgoingChangesPerSecond = diagnostics.OutgoingChangesPerSecond;
        ActiveSessionCount = diagnostics.ActiveSessionCount;
    }

    private void ResetDiagnostics()
    {
        IncomingChangesPerSecond = null;
        OutgoingChangesPerSecond = null;
        ActiveSessionCount = null;
    }

    private async Task StartServerAsync(CancellationToken cancellationToken)
    {
        try
        {
            Status = ServiceStatus.Starting;
            StatusMessage = null;

            if (string.IsNullOrEmpty(Path))
            {
                Status = ServiceStatus.Error;
                StatusMessage = "Path is not configured";
                return;
            }

            // Waited for here rather than in the factory, which is a synchronous Func<T> and cannot
            // await. This is the awaited start path, reached from ExecuteAsync, the Start operation and
            // ApplyConfigurationAsync, and never through this subject's own StopAsync, so parking here
            // cannot sit inside the handler's stop transition for this subject.
            while (!_rootManager.IsLoaded && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(RootLoadPollInterval, cancellationToken);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                Status = ServiceStatus.Stopped;
                return;
            }

            // The attachment survives a context detach, so on re-attach the handler re-invokes the
            // factory itself. Without this guard a restarted ExecuteAsync would attach a second server
            // alongside the one the handler just re-created.
            if (_attachment is null)
            {
                // The awaited overload, and CancellationToken.None rather than the caller's token. The
                // returned handle is the only record of the attachment, and the transition runs to
                // completion whatever the token does, so a cancelled wait would strand a live
                // attachment with nothing pointing at it and let the next start attach a second server.
                // The wait is bounded: the server is a BackgroundService whose StartAsync returns at its
                // first await, and a start appended during shutdown returns without creating anything.
                _attachment = await this.AttachHostedServiceAsync(CreateServer, CancellationToken.None);
            }

            UpdateFromAttachment();
            _logger.LogInformation("OPC UA server started for path: {Path}", Path);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = ServiceStatus.Stopped;
        }
        catch (Exception exception)
        {
            Status = ServiceStatus.Error;
            StatusMessage = exception.Message;
            _logger.LogError(exception, "Failed to start OPC UA server");
        }
    }

    /// <summary>
    /// Builds the server. Invoked by the handler on every attach, so it re-resolves the path and reads
    /// the configuration each time rather than capturing a snapshot: the target is a lookup into the
    /// graph, which may have replaced the subject at that path since the previous attach.
    /// </summary>
    private IOpcUaSubjectServer CreateServer()
    {
        // A synchronous factory can only signal a failed lookup by throwing. AttachHostedServiceAsync
        // rethrows it, and the catch above turns it into the same StatusMessage the inline null check
        // used to produce.
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

        return targetSubject.CreateOpcUaServer(configuration, _logger);
    }

    private async Task StopServerAsync(CancellationToken cancellationToken)
    {
        if (_attachment is not { } attachment)
        {
            return;
        }

        try
        {
            Status = ServiceStatus.Stopping;
            await this.DetachHostedServiceAsync(attachment, cancellationToken);
            _logger.LogInformation("OPC UA server stopped");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to stop OPC UA server");
        }
        finally
        {
            // Cleared from the subject's own attachment set rather than from the detach having
            // returned: the field must never read null while an attachment is still live, or the guard
            // in the start path attaches a second server over it. A cancelled wait still removed the
            // attachment, and a detach that threw before removing it did not.
            if (!this.GetHostedServiceAttachments().Contains(attachment))
            {
                _attachment = null;
            }

            Status = ServiceStatus.Stopped;
            ResetDiagnostics();
        }
    }
}
