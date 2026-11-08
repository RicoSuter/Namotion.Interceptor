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
    private readonly Func<AuthorizationPolicy> _getPolicy;
    private readonly AsyncLocal<AuthorizationPolicy?> _callPolicy = new();

    /// <param name="getPolicy">
    /// Gets the current policy that decides: fetched once for a call that <see cref="BeginCall"/> starts, so that a
    /// change of roles applies from the next call and a call stays consistent, and fetched for every question outside a
    /// call.
    /// </param>
    /// <param name="logger">
    /// Logs at debug level each denied <c>execute</c> and <c>history</c> with <paramref name="clientName"/> and the path
    /// of the member; null logs nothing.
    /// </param>
    /// <param name="clientName">The name of the client in the log, such as the agent whose tools ask.</param>
    /// <remarks>
    /// A denied <c>read</c> or <c>write</c> is not logged: the tools ask them for every member they list, for example
    /// whether a listed property is writable, so they are not refusals of something the client asked for.
    /// </remarks>
    public AuthorizationMcpAccessPolicy(Func<AuthorizationPolicy> getPolicy, ILogger? logger = null, string? clientName = null)
    {
        ArgumentNullException.ThrowIfNull(getPolicy);
        _getPolicy = getPolicy;
        _logger = logger;
        _clientName = clientName;
    }

    /// <summary>
    /// Gets the policy that decides: within a call that <see cref="BeginCall"/> started, the policy fetched for it;
    /// otherwise the current one, such as for a result that is enumerated after its call ended.
    /// </summary>
    public AuthorizationPolicy Policy => _callPolicy.Value ?? _getPolicy();

    /// <summary>
    /// Starts a tool call on the current async flow: fetches the current policy once and decides the whole call with it,
    /// until the returned scope is disposed.
    /// </summary>
    internal IDisposable BeginCall()
    {
        var previous = _callPolicy.Value;
        _callPolicy.Value = _getPolicy();
        return new CallScope(_callPolicy, previous);
    }

    /// <summary>
    /// Whether <paramref name="accessPolicy"/> allows the action on the member, as <see cref="IMcpAccessPolicy.IsAllowed"/>
    /// answers, but without logging a denial; true without a policy. For checks a tool makes for many members on its own,
    /// such as which pages a search reads, rather than for something the client asked for.
    /// </summary>
    internal static bool IsAllowedWithoutLogging(IMcpAccessPolicy? accessPolicy, RegisteredSubjectProperty member, string action) =>
        accessPolicy switch
        {
            null => true,
            AuthorizationMcpAccessPolicy authorization =>
                Actions.TryGetValue(action, out var permissionAction) && authorization.Policy.IsAllowed(member, permissionAction),
            _ => accessPolicy.IsAllowed(member, action)
        };

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

    // The path as the tools build it; the member name alone when the subject has no path from a root.
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

    private sealed class CallScope(AsyncLocal<AuthorizationPolicy?> callPolicy, AuthorizationPolicy? previous) : IDisposable
    {
        public void Dispose() => callPolicy.Value = previous;
    }
}
