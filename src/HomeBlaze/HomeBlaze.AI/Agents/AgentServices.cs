namespace HomeBlaze.AI.Agents;

/// <summary>
/// The services every agent depends on, resolved from dependency injection when an agent subject is created.
/// </summary>
public sealed class AgentServices(
    IAgentToolSource toolSource,
    AgentTurnTokenRegistry tokenRegistry,
    IAgentEndpointAddress endpointAddress,
    TimeProvider timeProvider)
{
    public IAgentToolSource ToolSource { get; } = toolSource;

    public AgentTurnTokenRegistry TokenRegistry { get; } = tokenRegistry;

    public IAgentEndpointAddress EndpointAddress { get; } = endpointAddress;

    public TimeProvider TimeProvider { get; } = timeProvider;
}
