using Namotion.Interceptor.OpcUa.Server;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Testing;

/// <summary>
/// Drives a running server's node state directly, so a test can produce inbound values the server
/// would never generate on its own (the connector only ever writes Good). Mutates under
/// <c>NodeManagerLock</c> and flushes in the same hold, which is how the SDK's own write service
/// reaches a node.
/// </summary>
internal static class OpcUaNodeStatusDriver
{
    /// <summary>
    /// Publishes a single value and status code onto the node backing <paramref name="property"/>.
    /// </summary>
    public static void Publish(
        IOpcUaSubjectServer server,
        PropertyReference property,
        object? value,
        StatusCode statusCode)
    {
        PublishMany(server, (property, value, statusCode));
    }

    /// <summary>
    /// Publishes several properties inside one lock hold, flushing only after every assignment, so a
    /// subscription delivers them in a single notification.
    /// </summary>
    public static void PublishMany(
        IOpcUaSubjectServer server,
        params (PropertyReference Property, object? Value, StatusCode StatusCode)[] updates)
    {
        var standardServer = (OpcUaStandardServer?)server.CurrentServer
            ?? throw new InvalidOperationException("The server is not running.");

        var nodeManagerLock = standardServer.NodeManagerLock
            ?? throw new InvalidOperationException("The server has no node manager yet.");

        var systemContext = standardServer.CurrentInstance.DefaultSystemContext;

        var nodes = new BaseDataVariableState[updates.Length];
        for (var index = 0; index < updates.Length; index++)
        {
            if (!server.TryGetVariableNode(updates[index].Property, out var node))
            {
                throw new InvalidOperationException(
                    $"No variable node exists for '{updates[index].Property.Name}'. Wait for it with TryGetVariableNode first.");
            }

            nodes[index] = node;
        }

        lock (nodeManagerLock)
        {
            for (var index = 0; index < updates.Length; index++)
            {
                // Value must be assigned before the status code: the SDK's setter resets an untouched
                // node's status to Good.
                nodes[index].Value = updates[index].Value;
                nodes[index].StatusCode = updates[index].StatusCode;
                nodes[index].Timestamp = DateTime.UtcNow;
            }

            foreach (var node in nodes)
            {
                node.ClearChangeMasks(systemContext, false);
            }
        }
    }
}
