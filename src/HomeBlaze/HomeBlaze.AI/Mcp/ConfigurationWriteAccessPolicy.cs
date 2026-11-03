using HomeBlaze.Services;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// Refuses set_property on every configuration property and allows everything else. An operation of the subject can
/// still change configuration as part of what it does.
/// </summary>
internal sealed class ConfigurationWriteAccessPolicy : IMcpAccessPolicy
{
    public static ConfigurationWriteAccessPolicy Instance { get; } = new();

    public bool IsAllowed(RegisteredSubjectProperty member, string action) =>
        action != McpAccessActions.Write || !member.IsConfigurationProperty();
}
