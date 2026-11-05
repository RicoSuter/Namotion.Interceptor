using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Mcp;

/// <summary>
/// An access policy that allows <see cref="McpAccessActions.Read"/>, compared ordinally, on every member and denies
/// every other action. The default <see cref="McpServerConfiguration.AccessPolicy"/>.
/// </summary>
public sealed class ReadOnlyMcpAccessPolicy : IMcpAccessPolicy
{
    private ReadOnlyMcpAccessPolicy()
    {
    }

    /// <summary>Gets the instance.</summary>
    public static ReadOnlyMcpAccessPolicy Instance { get; } = new();

    /// <inheritdoc />
    public bool IsAllowed(RegisteredSubjectProperty member, string action) =>
        string.Equals(action, McpAccessActions.Read, StringComparison.Ordinal);
}
