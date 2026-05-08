using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Namotion.Interceptor.Registry;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Wires together the <see cref="ConnectorSubjectMap{TExternalId}"/>, push trigger
/// (<see cref="OpcUaModelChangeEventHandler"/>), poll trigger (<see cref="OpcUaPeriodicResyncHandler"/>),
/// and the reconciler (<see cref="OpcUaSubjectLoader.ReconcileSubtreeAsync"/>).
/// Created by <see cref="OpcUaSubjectClientSource"/> after initial load to keep that class minimal.
/// </summary>
internal sealed class OpcUaStructureHandler : IAsyncDisposable
{
    private readonly ConnectorSubjectMap<NodeId> _subjectMap;
    private readonly OpcUaSubjectLoader _loader;
    private readonly OpcUaSubjectClientSource _clientSource;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly ILogger _logger;

    private OpcUaModelChangeEventHandler? _modelChangeEventHandler;
    private OpcUaPeriodicResyncHandler? _periodicResyncHandler;
    private Session? _session;
    private SubscriptionManager? _subscriptionManager;
    private IInterceptorSubject? _rootSubject;
    private NodeId? _rootNodeId;

    /// <summary>
    /// Gets the subject map that tracks NodeId-to-subject mappings.
    /// </summary>
    public ConnectorSubjectMap<NodeId> SubjectMap => _subjectMap;

    public OpcUaStructureHandler(
        OpcUaSubjectLoader loader,
        OpcUaSubjectClientSource clientSource,
        OpcUaClientConfiguration configuration,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(clientSource);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        _loader = loader;
        _clientSource = clientSource;
        _configuration = configuration;
        _logger = logger;
        _subjectMap = new ConnectorSubjectMap<NodeId>(clientSource.RootSubject.Context);
    }

    /// <summary>
    /// Populates the subject map from the loaded subject graph and starts the push/poll triggers.
    /// Called after initial load completes.
    /// </summary>
    public async Task StartAsync(
        IInterceptorSubject rootSubject,
        NodeId rootNodeId,
        Session session,
        SubscriptionManager subscriptionManager,
        CancellationToken cancellationToken)
    {
        _rootSubject = rootSubject;
        _rootNodeId = rootNodeId;
        _session = session;
        _subscriptionManager = subscriptionManager;

        PopulateSubjectMap(rootSubject);

        await StartTriggersAsync(session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Called on reconnect. Disposes old triggers and creates new ones with the new session.
    /// </summary>
    public async Task OnReconnectedAsync(Session session, CancellationToken cancellationToken)
    {
        await DisposeTriggersAsync().ConfigureAwait(false);

        _session = session;

        await StartTriggersAsync(session, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DisposeTriggersAsync().ConfigureAwait(false);
        _subjectMap.Dispose();
    }

    private async Task StartTriggersAsync(Session session, CancellationToken cancellationToken)
    {
        if (_configuration.EnableStructureSynchronization)
        {
            _modelChangeEventHandler = new OpcUaModelChangeEventHandler(OnModelChangeDetectedAsync, _logger);
            await _modelChangeEventHandler.SubscribeAsync(session, cancellationToken).ConfigureAwait(false);
        }

        if (_configuration.EnablePeriodicResynchronization)
        {
            _periodicResyncHandler = new OpcUaPeriodicResyncHandler(
                _configuration.PeriodicResynchronizationInterval,
                OnPeriodicResyncRequestedAsync,
                _logger);
            _periodicResyncHandler.Start();
        }
    }

    private async Task DisposeTriggersAsync()
    {
        if (_modelChangeEventHandler is not null)
        {
            await _modelChangeEventHandler.DisposeAsync().ConfigureAwait(false);
            _modelChangeEventHandler = null;
        }

        if (_periodicResyncHandler is not null)
        {
            _periodicResyncHandler.Dispose();
            _periodicResyncHandler = null;
        }
    }

    private async Task OnModelChangeDetectedAsync(
        ModelChangeStructureVerbMask verb,
        NodeId affectedNodeId,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Structure handler received model change: verb={Verb}, nodeId={NodeId}. Triggering reconciliation.",
            verb, affectedNodeId);

        await ReconcileFromRootAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task OnPeriodicResyncRequestedAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Periodic resync triggered. Reconciling from root.");
        await ReconcileFromRootAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReconcileFromRootAsync(CancellationToken cancellationToken)
    {
        var rootSubject = _rootSubject;
        var rootNodeId = _rootNodeId;
        var session = _session;
        var subscriptionManager = _subscriptionManager;

        if (rootSubject is null || rootNodeId is null || session is null || subscriptionManager is null)
        {
            _logger.LogWarning("Cannot reconcile: structure handler is not fully initialized.");
            return;
        }

        try
        {
            var newMonitoredItems = await _loader.ReconcileSubtreeAsync(
                rootSubject,
                rootNodeId,
                session,
                _subjectMap,
                subscriptionManager,
                cancellationToken).ConfigureAwait(false);

            if (newMonitoredItems.Count > 0)
            {
                _logger.LogInformation(
                    "Reconciliation added {Count} new monitored item(s).",
                    newMonitoredItems.Count);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during shutdown, don't log as error.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Reconciliation from root failed.");
        }
    }

    /// <summary>
    /// Walks the subject graph recursively and registers all subjects that have a NodeId
    /// stored in their Data dictionary into the <see cref="ConnectorSubjectMap{TExternalId}"/>.
    /// </summary>
    private void PopulateSubjectMap(IInterceptorSubject subject)
    {
        if (subject.TryGetData(OpcUaSubjectClientSource.SubjectNodeIdDataKey, out var nodeIdObj) &&
            nodeIdObj is NodeId nodeId)
        {
            _subjectMap.Add(nodeId, subject);
        }

        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null)
        {
            return;
        }

        foreach (var property in registeredSubject.Properties)
        {
            if (!property.CanContainSubjects)
            {
                continue;
            }

            foreach (var child in property.Children)
            {
                if (child.Subject is not null)
                {
                    PopulateSubjectMap(child.Subject);
                }
            }
        }
    }
}
