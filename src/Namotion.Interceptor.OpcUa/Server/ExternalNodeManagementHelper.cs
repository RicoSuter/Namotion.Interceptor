using Microsoft.Extensions.Logging;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Server;

/// <summary>
/// Helper class for external node management operations.
/// Provides methods to validate and process AddNodes/DeleteNodes requests
/// based on the server configuration and type registry.
///
/// Note: In the OPC UA SDK, AddNodes/DeleteNodes are handled at the NodeManager level
/// via the AddNode and DeleteNode methods. This class provides helper methods that
/// can be used by a custom node manager to implement external node management.
/// </summary>
public class ExternalNodeManagementHelper
{
    private readonly OpcUaServerConfiguration _configuration;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExternalNodeManagementHelper"/> class.
    /// </summary>
    /// <param name="configuration">The server configuration.</param>
    /// <param name="logger">The logger.</param>
    public ExternalNodeManagementHelper(OpcUaServerConfiguration configuration, ILogger logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Checks if external node management is enabled.
    /// </summary>
    public bool IsEnabled => _configuration.EnableExternalNodeManagement;

    /// <summary>
    /// Validates an AddNodes request and returns the resolved C# type if successful.
    /// </summary>
    /// <param name="nodesToAdd">The items to add.</param>
    /// <param name="namespaceTable">The namespace table for resolving NodeIds.</param>
    /// <param name="results">The results indicating success or failure for each item.</param>
    /// <returns>A list of (AddNodesItem, Type) tuples for successfully validated items.</returns>
    public IReadOnlyList<(AddNodesItem Item, Type CSharpType)> ValidateAddNodes(
        AddNodesItemCollection nodesToAdd,
        NamespaceTable namespaceTable,
        out AddNodesResultCollection results)
    {
        results = new AddNodesResultCollection();
        var validatedItems = new List<(AddNodesItem, Type)>();

        if (!_configuration.EnableExternalNodeManagement)
        {
            _logger.LogWarning("AddNodes request rejected: EnableExternalNodeManagement is disabled.");

            foreach (var nodeToAdd in nodesToAdd)
            {
                results.Add(new AddNodesResult
                {
                    StatusCode = StatusCodes.BadServiceUnsupported
                });
            }

            return validatedItems;
        }

        var typeRegistry = _configuration.TypeRegistry;
        if (typeRegistry is null)
        {
            _logger.LogWarning("AddNodes request rejected: TypeRegistry not configured.");

            foreach (var nodeToAdd in nodesToAdd)
            {
                results.Add(new AddNodesResult
                {
                    StatusCode = StatusCodes.BadNotSupported
                });
            }

            return validatedItems;
        }

        foreach (var nodeToAdd in nodesToAdd)
        {
            var typeDefinitionId = ExpandedNodeId.ToNodeId(
                nodeToAdd.TypeDefinition,
                namespaceTable);

            var csharpType = typeRegistry.ResolveType(typeDefinitionId);
            if (csharpType is null)
            {
                _logger.LogWarning(
                    "AddNodes: TypeDefinition '{TypeDefinition}' not registered for node '{BrowseName}'.",
                    typeDefinitionId, nodeToAdd.BrowseName?.Name);

                results.Add(new AddNodesResult
                {
                    StatusCode = StatusCodes.BadTypeDefinitionInvalid
                });
            }
            else
            {
                validatedItems.Add((nodeToAdd, csharpType));
                // Result will be added by the caller after processing
                results.Add(new AddNodesResult
                {
                    StatusCode = StatusCodes.Good
                });
            }
        }

        return validatedItems;
    }

    /// <summary>
    /// Validates a DeleteNodes request.
    /// </summary>
    /// <param name="nodesToDelete">The items to delete.</param>
    /// <param name="results">The results indicating success or failure for each item.</param>
    /// <returns>True if the request can proceed, false if it should be rejected entirely.</returns>
    public bool ValidateDeleteNodes(
        DeleteNodesItemCollection nodesToDelete,
        out StatusCodeCollection results)
    {
        results = new StatusCodeCollection();

        if (!_configuration.EnableExternalNodeManagement)
        {
            _logger.LogWarning("DeleteNodes request rejected: EnableExternalNodeManagement is disabled.");

            foreach (var nodeToDelete in nodesToDelete)
            {
                results.Add(StatusCodes.BadServiceUnsupported);
            }

            return false;
        }

        // All items pass initial validation - actual deletion validation happens per-item
        foreach (var nodeToDelete in nodesToDelete)
        {
            results.Add(StatusCodes.Good);
        }

        return true;
    }
}
