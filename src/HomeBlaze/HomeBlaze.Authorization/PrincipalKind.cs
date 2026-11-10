namespace HomeBlaze.Authorization;

/// <summary>
/// The kind of principal a policy is compiled for, which selects its system rules, see <see cref="SystemRules.For"/>.
/// </summary>
public enum PrincipalKind
{
    /// <summary>An agent or an MCP client.</summary>
    NonHuman,

    /// <summary>A signed-in person.</summary>
    Person,

    /// <summary>A visitor who is not signed in.</summary>
    Anonymous
}
