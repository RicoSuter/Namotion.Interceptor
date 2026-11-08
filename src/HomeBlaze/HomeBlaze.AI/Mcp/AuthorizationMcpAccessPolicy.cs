using System.Collections.Frozen;
using HomeBlaze.Authorization;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// An <see cref="AuthorizationPolicy"/> as the access policy of the MCP tools. The actions <c>read</c>, <c>write</c>,
/// <c>execute</c> and <c>history</c> are matched ignoring case; any other action is denied.
/// </summary>
public sealed class AuthorizationMcpAccessPolicy : IMcpAccessPolicy
{
    private static readonly FrozenDictionary<string, PermissionAction> Actions =
        AuthorizationActions.All.ToFrozenDictionary(action => action.Name, StringComparer.OrdinalIgnoreCase);

    private readonly Func<AuthorizationPolicy> _getPolicy;
    private readonly AsyncLocal<AuthorizationPolicy?> _callPolicy = new();

    /// <param name="getPolicy">
    /// Gets the current policy that decides: fetched once for a call that <see cref="BeginCall"/> starts, so that a
    /// change of roles applies from the next call and a call stays consistent, and fetched for every question outside a
    /// call.
    /// </param>
    public AuthorizationMcpAccessPolicy(Func<AuthorizationPolicy> getPolicy)
    {
        ArgumentNullException.ThrowIfNull(getPolicy);
        _getPolicy = getPolicy;
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

    /// <inheritdoc />
    public bool IsAllowed(RegisteredSubjectProperty member, string action) =>
        Actions.TryGetValue(action, out var permissionAction) && Policy.IsAllowed(member, permissionAction);

    private sealed class CallScope(AsyncLocal<AuthorizationPolicy?> callPolicy, AuthorizationPolicy? previous) : IDisposable
    {
        public void Dispose() => callPolicy.Value = previous;
    }
}
