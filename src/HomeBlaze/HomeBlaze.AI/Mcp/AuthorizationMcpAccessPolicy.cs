using System.Collections.Frozen;
using HomeBlaze.Authorization;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// An <see cref="AuthorizationPolicy"/> as the access policy of the MCP tools. The actions <c>read</c>, <c>write</c>,
/// <c>execute</c> and <c>history</c> are matched ignoring case; any other action is denied.
/// </summary>
public sealed class AuthorizationMcpAccessPolicy : IMcpAccessPolicy
{
    private static readonly FrozenDictionary<string, PermissionAction> Actions =
        AuthorizationActions.All.ToFrozenDictionary(action => action.Name, StringComparer.OrdinalIgnoreCase);

    private readonly ILogger? _logger;
    private readonly string? _clientName;

    /// <param name="policy">The policy that decides.</param>
    /// <param name="logger">
    /// Logs at debug level each denied <c>execute</c> and <c>history</c> with <paramref name="clientName"/> and the path
    /// of the member; null logs nothing.
    /// </param>
    /// <param name="clientName">The name of the client in the log, such as the agent whose tools ask.</param>
    /// <remarks>
    /// A denied <c>read</c> or <c>write</c> is not logged: the tools ask them for every member they list, for example
    /// whether a listed property is writable, so they are not refusals of something the client asked for.
    /// </remarks>
    public AuthorizationMcpAccessPolicy(AuthorizationPolicy policy, ILogger? logger = null, string? clientName = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Policy = policy;
        _logger = logger;
        _clientName = clientName;
    }

    /// <summary>Gets the policy that decides.</summary>
    public AuthorizationPolicy Policy { get; }

    /// <inheritdoc />
    public bool IsAllowed(RegisteredSubjectProperty member, string action)
    {
        if (!Actions.TryGetValue(action, out var permissionAction))
        {
            return false;
        }

        if (Policy.IsAllowed(member, permissionAction))
        {
            return true;
        }

        if (_logger is not null &&
            (permissionAction == AuthorizationActions.Execute || permissionAction == AuthorizationActions.History) &&
            _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "The access policy of {Client} denied {Action} on {Member}.", _clientName, permissionAction.Name, GetPath(member));
        }

        return false;
    }

    // The path as the tools write it; the member name alone when the subject has no path from a root.
    private static string GetPath(RegisteredSubjectProperty member)
    {
        var parents = member.Parent.Parents;
        if (parents.IsDefaultOrEmpty)
        {
            return "/" + member.Name;
        }

        var parent = parents[0];
        var subjectPath = parent.Property.TryGetPath(McpAuthorization.PathProvider, null, parent.Index);
        return subjectPath is null ? member.Name : "/" + subjectPath + "/" + member.Name;
    }
}
