using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Mcp;

/// <summary>
/// Access checks of a tool configuration, for the tools of this package and of an application alike.
/// </summary>
public static class McpServerConfigurationExtensions
{
    /// <summary>
    /// Whether <see cref="McpServerConfiguration.AccessPolicy"/> allows the action on the member; true when there is
    /// no policy.
    /// </summary>
    public static bool IsAllowed(this McpServerConfiguration configuration, RegisteredSubjectProperty member, string action)
        => configuration.AccessPolicy?.IsAllowed(member, action) ?? true;
}
