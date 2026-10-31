using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// An access policy that allows every action on every member, also those the system rules deny, for tools whose
/// results no client sees, such as the listing of tool names.
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
