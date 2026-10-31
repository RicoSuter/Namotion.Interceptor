using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.AI.Agents;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Authorization.Tests.Subjects;

/// <summary>
/// A concrete agent, derived from the agents' base type as the real agent classes are.
/// </summary>
[InterceptorSubject]
public partial class TestAgent : LlmAgentBase
{
    [Configuration]
    public partial string? Model { get; set; }

    [Operation]
    public Task StopAsync() => Task.CompletedTask;
}
