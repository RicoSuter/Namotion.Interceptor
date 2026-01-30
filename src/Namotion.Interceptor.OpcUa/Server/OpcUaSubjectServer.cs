using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Server;

namespace Namotion.Interceptor.OpcUa.Server;

internal class OpcUaSubjectServer : StandardServer
{
    private readonly ILogger _logger;
    private readonly CustomNodeManagerFactory _nodeManagerFactory;

    private IServerInternal? _server;
    private SessionEventHandler? _sessionCreatedHandler;
    private SessionEventHandler? _sessionClosingHandler;

    public OpcUaSubjectServer(IInterceptorSubject subject, OpcUaSubjectServerBackgroundService source, OpcUaServerConfiguration configuration, ILogger logger)
    {
        _logger = logger;
        _nodeManagerFactory = new CustomNodeManagerFactory(subject, source, configuration, logger);
        AddNodeManager(_nodeManagerFactory);
    }

    /// <summary>
    /// Gets the custom node manager, if the server has started.
    /// </summary>
    public CustomNodeManager? NodeManager => _nodeManagerFactory.NodeManager;

    public void ClearPropertyData()
    {
        _nodeManagerFactory.NodeManager?.ClearPropertyData();
    }

    public void RemoveSubjectNodes(IInterceptorSubject subject)
    {
        _nodeManagerFactory.NodeManager?.RemoveSubjectNodes(subject);
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
        if (disposing && _server is not null)
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

        base.Dispose(disposing);
    }

    /// <summary>
    /// Handles the AddNodes service request asynchronously.
    /// Creates subjects in the C# model based on the TypeDefinition and creates corresponding OPC UA nodes.
    /// </summary>
    public override Task<AddNodesResponse> AddNodesAsync(
        SecureChannelContext secureChannelContext,
        RequestHeader requestHeader,
        AddNodesItemCollection nodesToAdd,
        CancellationToken cancellationToken)
    {
        ValidateRequest(requestHeader);

        var results = new AddNodesResultCollection();
        var diagnosticInfos = new DiagnosticInfoCollection();

        var nodeManager = NodeManager;
        if (nodeManager is null)
        {
            _logger.LogWarning("AddNodes: NodeManager is not available.");
            foreach (var item in nodesToAdd)
            {
                results.Add(new AddNodesResult { StatusCode = StatusCodes.BadNotSupported });
            }

            return Task.FromResult(new AddNodesResponse
            {
                ResponseHeader = CreateResponse(requestHeader, StatusCodes.Good),
                Results = results,
                DiagnosticInfos = diagnosticInfos
            });
        }

        // Check if external management is enabled at NodeManager level
        if (!nodeManager.IsExternalNodeManagementEnabled)
        {
            _logger.LogWarning("AddNodes: External node management is disabled.");
            foreach (var item in nodesToAdd)
            {
                results.Add(new AddNodesResult { StatusCode = StatusCodes.BadServiceUnsupported });
            }

            return Task.FromResult(new AddNodesResponse
            {
                ResponseHeader = CreateResponse(requestHeader, StatusCodes.Good),
                Results = results,
                DiagnosticInfos = diagnosticInfos
            });
        }

        var namespaceTable = _server?.NamespaceUris ?? new NamespaceTable();

        // Process each item
        foreach (var item in nodesToAdd)
        {
            try
            {
                var typeDefinitionId = ExpandedNodeId.ToNodeId(item.TypeDefinition, namespaceTable);
                var parentNodeId = ExpandedNodeId.ToNodeId(item.ParentNodeId, namespaceTable);
                var browseName = item.BrowseName ?? new QualifiedName("Unknown", nodeManager.NamespaceIndexes[0]);

                // Use the CustomNodeManager's existing AddSubjectFromExternal method
                var (subject, nodeState) = nodeManager.AddSubjectFromExternal(
                    typeDefinitionId,
                    browseName,
                    parentNodeId);

                if (subject is not null && nodeState is not null)
                {
                    results.Add(new AddNodesResult
                    {
                        StatusCode = StatusCodes.Good,
                        AddedNodeId = nodeState.NodeId
                    });
                }
                else
                {
                    results.Add(new AddNodesResult
                    {
                        StatusCode = StatusCodes.BadTypeDefinitionInvalid
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AddNodes: Failed to process item '{BrowseName}'.", item.BrowseName?.Name);
                results.Add(new AddNodesResult { StatusCode = StatusCodes.BadUnexpectedError });
            }
        }

        // Flush model change events
        nodeManager.FlushModelChangeEvents();

        return Task.FromResult(new AddNodesResponse
        {
            ResponseHeader = CreateResponse(requestHeader, StatusCodes.Good),
            Results = results,
            DiagnosticInfos = diagnosticInfos
        });
    }

    /// <summary>
    /// Handles the DeleteNodes service request asynchronously.
    /// Removes subjects from the C# model and removes corresponding OPC UA nodes.
    /// </summary>
    public override Task<DeleteNodesResponse> DeleteNodesAsync(
        SecureChannelContext secureChannelContext,
        RequestHeader requestHeader,
        DeleteNodesItemCollection nodesToDelete,
        CancellationToken cancellationToken)
    {
        ValidateRequest(requestHeader);

        var results = new StatusCodeCollection();
        var diagnosticInfos = new DiagnosticInfoCollection();

        var nodeManager = NodeManager;
        if (nodeManager is null)
        {
            _logger.LogWarning("DeleteNodes: NodeManager is not available.");
            foreach (var item in nodesToDelete)
            {
                results.Add(StatusCodes.BadNotSupported);
            }

            return Task.FromResult(new DeleteNodesResponse
            {
                ResponseHeader = CreateResponse(requestHeader, StatusCodes.Good),
                Results = results,
                DiagnosticInfos = diagnosticInfos
            });
        }

        // Check if external management is enabled at NodeManager level
        if (!nodeManager.IsExternalNodeManagementEnabled)
        {
            _logger.LogWarning("DeleteNodes: External node management is disabled.");
            foreach (var item in nodesToDelete)
            {
                results.Add(StatusCodes.BadServiceUnsupported);
            }

            return Task.FromResult(new DeleteNodesResponse
            {
                ResponseHeader = CreateResponse(requestHeader, StatusCodes.Good),
                Results = results,
                DiagnosticInfos = diagnosticInfos
            });
        }

        var namespaceTable = _server?.NamespaceUris ?? new NamespaceTable();

        // Delete each node
        foreach (var item in nodesToDelete)
        {
            try
            {
                var nodeId = ExpandedNodeId.ToNodeId(item.NodeId, namespaceTable);

                // Use the CustomNodeManager's existing RemoveSubjectFromExternal method
                var success = nodeManager.RemoveSubjectFromExternal(nodeId);

                results.Add(success ? StatusCodes.Good : StatusCodes.BadNodeIdUnknown);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DeleteNodes: Failed to delete node '{NodeId}'.", item.NodeId);
                results.Add(StatusCodes.BadUnexpectedError);
            }
        }

        // Flush model change events
        nodeManager.FlushModelChangeEvents();

        return Task.FromResult(new DeleteNodesResponse
        {
            ResponseHeader = CreateResponse(requestHeader, StatusCodes.Good),
            Results = results,
            DiagnosticInfos = diagnosticInfos
        });
    }
}
