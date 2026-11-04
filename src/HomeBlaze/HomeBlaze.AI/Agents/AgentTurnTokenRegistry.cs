using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// Issues the bearer token of a running turn and maps it to the turn's tools.
/// </summary>
public sealed class AgentTurnTokenRegistry
{
    // Keyed by the token's hash, so the lookup's timing reveals nothing about a valid token.
    private readonly ConcurrentDictionary<string, AgentToolSet> _toolSets = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers the tools of a turn. Dispose the registration when the turn ends.
    /// </summary>
    internal AgentTurnRegistration Register(AgentToolSet toolSet)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var key = Hash(token);
        _toolSets[key] = toolSet;
        return new AgentTurnRegistration(token, () => _toolSets.TryRemove(key, out _));
    }

    /// <summary>
    /// Gets the tools of the turn the token belongs to.
    /// </summary>
    public bool TryGetToolSet(string token, [NotNullWhen(true)] out AgentToolSet? toolSet) =>
        _toolSets.TryGetValue(Hash(token), out toolSet);

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

/// <summary>
/// The token of a running turn. Disposing revokes it.
/// </summary>
internal sealed class AgentTurnRegistration : IDisposable
{
    private Action? _revoke;

    internal AgentTurnRegistration(string token, Action revoke)
    {
        Token = token;
        _revoke = revoke;
    }

    /// <summary>The bearer token.</summary>
    public string Token { get; }

    public void Dispose() => Interlocked.Exchange(ref _revoke, null)?.Invoke();
}
