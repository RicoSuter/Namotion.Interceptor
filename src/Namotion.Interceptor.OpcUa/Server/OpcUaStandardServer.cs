using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Server;

namespace Namotion.Interceptor.OpcUa.Server;

internal class OpcUaStandardServer : StandardServer
{
    private readonly ILogger _logger;
    private readonly OpcUaServerConfiguration _configuration;
    private readonly CustomNodeManagerFactory _nodeManagerFactory;

    private IServerInternal? _server;
    private SessionEventHandler? _sessionCreatedHandler;
    private SessionEventHandler? _sessionClosingHandler;
    private List<ITransportListener>? _savedTransportListeners;

    public OpcUaStandardServer(IInterceptorSubject subject, OpcUaSubjectServer source, OpcUaServerConfiguration configuration, ILogger logger)
    {
        _logger = logger;
        _configuration = configuration;
        _nodeManagerFactory = new CustomNodeManagerFactory(subject, source, configuration, logger);
        AddNodeManager(_nodeManagerFactory);
    }

    // TODO: Remove saved listener workaround when https://github.com/OPCFoundation/UA-.NETStandard/pull/3561 is released.
    // Workaround: ServerBase.StopAsync calls Close() then Clear() on the listener list.
    // TcpTransportListener.Close() only stops listening sockets — it does NOT call Dispose().
    // ServerBase.Dispose() later iterates TransportListeners to dispose them, but the list is
    // already empty. So TcpTransportListener.Dispose() never runs, leaking timers, channels,
    // and buffer managers. We save listener references before StopAsync clears the list,
    // then manually dispose them after shutdown.

    /// <summary>
    /// Closes all transport listeners to stop accepting new connections.
    /// Must be called before closing sessions during shutdown to prevent
    /// clients from reconnecting while the server is shutting down.
    /// Also saves references so they can be properly disposed later,
    /// since the SDK's StopAsync clears the TransportListeners list
    /// before Dispose can process them.
    /// </summary>
    public void CloseTransportListeners()
    {
        _savedTransportListeners ??= [.. TransportListeners];
        foreach (var listener in _savedTransportListeners)
        {
            try { listener.Close(); } catch (Exception ex) { _logger.LogDebug(ex, "Error closing transport listener."); }
        }
    }

    /// <summary>
    /// Disposes all saved transport listeners. Must be called after shutdown
    /// because the SDK's StopAsync clears the TransportListeners list before
    /// Dispose can process them, causing TcpTransportListener.Dispose() to
    /// never run (leaking timers, channels, and buffer managers).
    /// </summary>
    public void DisposeTransportListeners()
    {
        if (_savedTransportListeners is null)
        {
            return;
        }

        foreach (var listener in _savedTransportListeners)
        {
            try { (listener as IDisposable)?.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Error disposing transport listener."); }
        }

        _savedTransportListeners = null;
    }

    /// <summary>
    /// Gets the custom node manager instance, or null if the server hasn't been started yet.
    /// </summary>
    internal CustomNodeManager? GetNodeManager() => _nodeManagerFactory.NodeManager;

    /// <summary>
    /// Gets the node manager's lock object for thread-safe node updates.
    /// This is the same lock used by the SDK for Read/Write operations.
    /// </summary>
    internal object? NodeManagerLock => _nodeManagerFactory.NodeManager?.Lock;

    public void ClearPropertyData()
    {
        _nodeManagerFactory.NodeManager?.ClearPropertyData();
    }

    public void RemoveSubjectNodes(IInterceptorSubject subject)
    {
        _nodeManagerFactory.NodeManager?.RemoveSubjectNodes(subject);
    }

    /// <summary>
    /// Handles AddNodes requests from OPC UA clients.
    /// When AllowRemoteNodeManagement is enabled, delegates to the CustomNodeManager
    /// to create subjects in the local model and OPC UA nodes.
    /// </summary>
    public override async Task<AddNodesResponse> AddNodesAsync(
        SecureChannelContext secureChannelContext,
        RequestHeader requestHeader,
        AddNodesItemCollection nodesToAdd,
        CancellationToken cancellationToken)
    {
        if (!_configuration.AllowRemoteNodeManagement)
        {
            return await base.AddNodesAsync(secureChannelContext, requestHeader, nodesToAdd, cancellationToken).ConfigureAwait(false);
        }

        var nodeManager = GetNodeManager();
        if (nodeManager is null)
        {
            return await base.AddNodesAsync(secureChannelContext, requestHeader, nodesToAdd, cancellationToken).ConfigureAwait(false);
        }

        var nodeManagerLock = NodeManagerLock;
        if (nodeManagerLock is null)
        {
            return await base.AddNodesAsync(secureChannelContext, requestHeader, nodesToAdd, cancellationToken).ConfigureAwait(false);
        }

        var results = new AddNodesResultCollection(nodesToAdd.Count);
        var diagnosticInfos = new DiagnosticInfoCollection(nodesToAdd.Count);

        lock (nodeManagerLock)
        {
            foreach (var item in nodesToAdd)
            {
                try
                {
                    // Check if the parent is a container folder (collection/dictionary)
                    var parentNodeId = ExpandedNodeId.ToNodeId(item.ParentNodeId, CurrentInstance.NamespaceUris);
                    if (parentNodeId is not null)
                    {
                        var (containerOwner, containerProperty) = nodeManager.FindContainerOwner(parentNodeId);
                        if (containerOwner is not null && containerProperty is not null)
                        {
                            // Parent is a container folder. The browse name is the key/index.
                            // We need to pass through to HandleRemoteAddNode which will
                            // find the right property using FindPropertyForBrowseName on the container owner.
                            var containerItem = new AddNodesItem
                            {
                                ParentNodeId = item.ParentNodeId,
                                ReferenceTypeId = item.ReferenceTypeId,
                                RequestedNewNodeId = item.RequestedNewNodeId,
                                BrowseName = item.BrowseName,
                                NodeClass = item.NodeClass,
                                NodeAttributes = item.NodeAttributes,
                                TypeDefinition = item.TypeDefinition
                            };

                            // Temporarily change the ParentNodeId to the owner subject's NodeId
                            // so HandleRemoteAddNode can find the right parent subject
                            // But actually, for dictionaries the parent in the AddNodes call IS
                            // the container folder, and we need to resolve to the parent subject.
                            // The FindPropertyForBrowseName on the containerOwner will match
                            // the dictionary property with browseNameStr as key.

                            // Create a modified item with the parent set to the container owner
                            var ownerNodeId = nodeManager.TryGetNodeIdForSubject(containerOwner);
                            if (ownerNodeId is not null)
                            {
                                containerItem.ParentNodeId = new ExpandedNodeId(ownerNodeId);
                            }

                            results.Add(nodeManager.HandleRemoteAddNode(containerItem));
                        }
                        else
                        {
                            results.Add(nodeManager.HandleRemoteAddNode(item));
                        }
                    }
                    else
                    {
                        results.Add(nodeManager.HandleRemoteAddNode(item));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing AddNodes request for browse name '{BrowseName}'.", item.BrowseName);
                    results.Add(new AddNodesResult
                    {
                        StatusCode = StatusCodes.BadInternalError,
                        AddedNodeId = NodeId.Null
                    });
                }

                diagnosticInfos.Add(null);
            }
        }

        var response = new AddNodesResponse
        {
            ResponseHeader = CreateResponseHeader(requestHeader, StatusCodes.Good),
            Results = results,
            DiagnosticInfos = diagnosticInfos
        };

        return response;
    }

    /// <summary>
    /// Handles DeleteNodes requests from OPC UA clients.
    /// When AllowRemoteNodeManagement is enabled, delegates to the CustomNodeManager
    /// to remove subjects from the local model and clean up OPC UA nodes.
    /// </summary>
    public override async Task<DeleteNodesResponse> DeleteNodesAsync(
        SecureChannelContext secureChannelContext,
        RequestHeader requestHeader,
        DeleteNodesItemCollection nodesToDelete,
        CancellationToken cancellationToken)
    {
        if (!_configuration.AllowRemoteNodeManagement)
        {
            return await base.DeleteNodesAsync(secureChannelContext, requestHeader, nodesToDelete, cancellationToken).ConfigureAwait(false);
        }

        var nodeManager = GetNodeManager();
        if (nodeManager is null)
        {
            return await base.DeleteNodesAsync(secureChannelContext, requestHeader, nodesToDelete, cancellationToken).ConfigureAwait(false);
        }

        var nodeManagerLock = NodeManagerLock;
        if (nodeManagerLock is null)
        {
            return await base.DeleteNodesAsync(secureChannelContext, requestHeader, nodesToDelete, cancellationToken).ConfigureAwait(false);
        }

        var results = new StatusCodeCollection(nodesToDelete.Count);
        var diagnosticInfos = new DiagnosticInfoCollection(nodesToDelete.Count);

        lock (nodeManagerLock)
        {
            foreach (var item in nodesToDelete)
            {
                try
                {
                    results.Add(nodeManager.HandleRemoteDeleteNode(item));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing DeleteNodes request for NodeId '{NodeId}'.", item.NodeId);
                    results.Add(StatusCodes.BadInternalError);
                }

                diagnosticInfos.Add(null);
            }
        }

        var response = new DeleteNodesResponse
        {
            ResponseHeader = CreateResponseHeader(requestHeader, StatusCodes.Good),
            Results = results,
            DiagnosticInfos = diagnosticInfos
        };

        return response;
    }

    private static ResponseHeader CreateResponseHeader(RequestHeader requestHeader, StatusCode statusCode)
    {
        return new ResponseHeader
        {
            Timestamp = DateTime.UtcNow,
            RequestHandle = requestHeader?.RequestHandle ?? 0,
            ServiceResult = statusCode
        };
    }

    protected override void OnServerStarted(IServerInternal server)
    {
        // Unsubscribe any existing handlers to prevent accumulation on server restart
        if (_server is not null && _sessionCreatedHandler is not null)
        {
            _server.SessionManager.SessionCreated -= _sessionCreatedHandler;
        }
        if (_server is not null && _sessionClosingHandler is not null)
        {
            _server.SessionManager.SessionClosing -= _sessionClosingHandler;
        }

        _server = server;

        _sessionCreatedHandler = (session, _) =>
        {
            _logger.LogInformation("OPC UA session {SessionId} with user {UserIdentity} created.", session.Id, session.Identity.DisplayName);
        };

        _sessionClosingHandler = (session, _) =>
        {
            _logger.LogInformation("OPC UA session {SessionId} with user {UserIdentity} closing.", session.Id, session.Identity.DisplayName);
        };

        server.SessionManager.SessionCreated += _sessionCreatedHandler;
        server.SessionManager.SessionClosing += _sessionClosingHandler;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_server is not null)
            {
                if (_sessionCreatedHandler is not null)
                {
                    _server.SessionManager.SessionCreated -= _sessionCreatedHandler;
                }

                if (_sessionClosingHandler is not null)
                {
                    _server.SessionManager.SessionClosing -= _sessionClosingHandler;
                }

                _server = null;
            }
        }

        base.Dispose(disposing);
    }
}
