using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.AI.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Authorization.Tests.Subjects;

/// <summary>
/// A concrete agent, derived from the agents' base type as the real agent classes are. The base only keeps the
/// services it is given until a turn runs, so a subject that is never started needs none of them.
/// </summary>
[InterceptorSubject]
public partial class TestAgent : LlmAgentBase
{
    public TestAgent()
        : base(new AgentServices(null!, new AgentTurnTokenRegistry(), null!, TimeProvider.System, null!), NullLogger.Instance)
    {
    }

    [Operation]
    public Task StopAsync() => Task.CompletedTask;

    protected override Task<AgentTurnResult> ExecuteTurnAsync(
        AgentTurnRequest request, IProgress<AgentTurnUpdate> progress, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    protected override Task ResetSessionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
