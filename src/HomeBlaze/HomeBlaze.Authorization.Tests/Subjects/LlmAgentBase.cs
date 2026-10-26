using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

// A stand-in with the full name of the agents' base type, which this test project does not reference.
namespace HomeBlaze.AI.Agents;

[InterceptorSubject]
public abstract partial class LlmAgentBase
{
    [State]
    public partial string? Status { get; set; }
}
