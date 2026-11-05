using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Mcp;

/// <summary>
/// An access policy that allows every action on every member, so a client may read and write every property the tools
/// list and a custom tool allows every action of its own.
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
