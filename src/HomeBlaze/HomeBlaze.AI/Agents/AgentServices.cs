namespace HomeBlaze.AI.Agents;

/// <summary>
/// The services every agent depends on, resolved from dependency injection when an agent subject is created.
/// </summary>
public sealed class AgentServices
{
    public AgentServices(
        IAgentToolSource toolSource,
        AgentTurnTokenRegistry tokenRegistry,
        IAgentEndpointAddress endpointAddress,
        TimeProvider timeProvider)
    {
        ToolSource = toolSource;
        TokenRegistry = tokenRegistry;
        EndpointAddress = endpointAddress;
        TimeProvider = timeProvider;
    }

    public IAgentToolSource ToolSource { get; }

    public AgentTurnTokenRegistry TokenRegistry { get; }

    public IAgentEndpointAddress EndpointAddress { get; }

    public TimeProvider TimeProvider { get; }
}
