using HomeBlaze.AI.Agents;
using HomeBlaze.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;

namespace HomeBlaze.Authentication.Blazor.Tests;

/// <summary>An agent that is never started, for the permissions of its members.</summary>
public class PermissionsTestAgent : LlmAgentBase
{
    public PermissionsTestAgent(AuthorizationService authorizationService)
        : base(CreateServices(authorizationService), NullLogger.Instance)
    {
    }

    /// <summary>Creates services without tools or an endpoint, for an agent that is never started.</summary>
    public static AgentServices CreateServices(AuthorizationService authorizationService) =>
        new(new NoTools(), new AgentTurnTokenRegistry(), new NoEndpoint(), TimeProvider.System, authorizationService);

    protected override Task<AgentTurnResult> ExecuteTurnAsync(AgentTurnRequest request, IProgress<AgentTurnUpdate> progress, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    protected override Task ResetSessionAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private sealed class NoTools : IAgentToolSource
    {
        public IReadOnlyList<McpToolInfo> CreateTools(IMcpAccessPolicy accessPolicy) => [];
    }

    private sealed class NoEndpoint : IAgentEndpointAddress
    {
        public Uri? GetAgentEndpoint() => null;
    }
}
