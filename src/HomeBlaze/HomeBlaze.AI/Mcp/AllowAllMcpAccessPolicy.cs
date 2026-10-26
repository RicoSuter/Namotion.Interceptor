using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// An access policy that allows every action on every member, for tools whose scope the operator chose or whose
/// results no client sees, such as the state of an agent's watch paths or the listing of tool names.
/// </summary>
public sealed class AllowAllMcpAccessPolicy : IMcpAccessPolicy
{
    private AllowAllMcpAccessPolicy()
    {
    }

    /// <summary>Gets the instance.</summary>
    public static AllowAllMcpAccessPolicy Instance { get; } = new();

    /// <inheritdoc />
    public bool IsAllowed(RegisteredSubjectProperty member, string action) => true;
}
