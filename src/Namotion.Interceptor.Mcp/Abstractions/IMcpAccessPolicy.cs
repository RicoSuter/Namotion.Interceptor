using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Mcp.Abstractions;

/// <summary>
/// Decides whether the client of a tool configuration may perform an action on a member: a property, an attribute or
/// a method registered as a property. The tools ask it for <see cref="McpAccessActions.Read"/> and
/// <see cref="McpAccessActions.Write"/>; tools of an application may ask it for their own actions.
/// </summary>
public interface IMcpAccessPolicy
{
    /// <summary>
    /// Whether the action is allowed on the member. May be called concurrently, so it must be thread-safe.
    /// </summary>
    /// <param name="member">The member.</param>
    /// <param name="action">The action, for example <see cref="McpAccessActions.Read"/>.</param>
    bool IsAllowed(RegisteredSubjectProperty member, string action);
}
