namespace HomeBlaze.AI.Agents;

/// <summary>
/// Knows where the agent MCP route is reachable from the local machine.
/// </summary>
public interface IAgentEndpointAddress
{
    /// <summary>
    /// The absolute URI of the agent route, or null while the web server has no address.
    /// </summary>
    Uri? GetAgentEndpoint();
}
