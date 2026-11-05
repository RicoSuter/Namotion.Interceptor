namespace HomeBlaze.Authorization;

/// <summary>
/// The access of the clients of the external MCP endpoint: the roles of the built-in user
/// <see cref="NameRules.McpUserName"/> and its optional key. Thread-safe.
/// </summary>
public interface IMcpClientAccess
{
    /// <summary>The names of the roles of the built-in user <see cref="NameRules.McpUserName"/>; empty when it does not exist.</summary>
    IReadOnlyCollection<string> Roles { get; }

    /// <summary>Whether a key exists, which clients must then present.</summary>
    bool RequiresKey { get; }

    /// <summary>Whether <paramref name="presentedKey"/> is the key, compared in constant time; false when there is no key.</summary>
    bool Verify(string presentedKey);
}
