using System.Collections.Immutable;
using Namotion.Interceptor.Authorization;

namespace HomeBlaze.Authorization;

/// <summary>
/// The roles defined in code, always available and not editable. Their names are reserved for custom roles.
/// </summary>
public static class BuiltInRoles
{
    /// <summary>The name of <see cref="Reader"/>.</summary>
    public const string ReaderName = "reader";

    /// <summary>The name of <see cref="Operator"/>.</summary>
    public const string OperatorName = "operator";

    /// <summary>The name of <see cref="Everything"/>.</summary>
    public const string EverythingName = "everything";

    /// <summary>
    /// Reads every member and its history and executes queries. Secrets stay unreadable through the system rules.
    /// </summary>
    public static AuthorizationRole Reader { get; } = new()
    {
        Name = ReaderName,
        // Without selectors a rule matches every member, also one without a marker, such as a derived title.
        Allow =
        [
            new PermissionRule { Actions = [PermissionAction.Read, AuthorizationActions.History] },
            new PermissionRule { Tags = [MemberTags.Query], Actions = [AuthorizationActions.Execute] }
        ]
    };

    /// <summary>
    /// Includes <see cref="Reader"/>, writes every member and executes operations. Configuration, secrets and operations
    /// that require confirmation stay blocked through the system rules.
    /// </summary>
    public static AuthorizationRole Operator { get; } = new()
    {
        Name = OperatorName,
        Includes = [ReaderName],
        Allow =
        [
            new PermissionRule { Actions = [PermissionAction.Write] },
            new PermissionRule { Tags = [MemberTags.Operation], Actions = [AuthorizationActions.Execute] }
        ]
    };

    /// <summary>
    /// Allows every action on every member; the system rules still apply.
    /// </summary>
    public static AuthorizationRole Everything { get; } = new()
    {
        Name = EverythingName,
        // Without selectors a rule matches every member, also one of a subject whose paths cannot be listed.
        Allow = [new PermissionRule { Actions = AuthorizationActions.All }]
    };

    /// <summary>All built-in roles.</summary>
    public static ImmutableArray<AuthorizationRole> All { get; } = [Reader, Operator, Everything];

    /// <summary>Gets what the built-in role named <paramref name="name"/> allows, for people to read, or null for another name.</summary>
    public static string? GetDescription(string name) => name switch
    {
        ReaderName => "Reads every member and its history and executes queries.",
        OperatorName => $"Includes {ReaderName}, writes every member and executes operations.",
        EverythingName => "Allows every action on every member.",
        _ => null
    };

    /// <summary>Gets whether the name is the name of a built-in role.</summary>
    public static bool IsBuiltIn(string name) =>
        string.Equals(name, ReaderName, StringComparison.Ordinal) ||
        string.Equals(name, OperatorName, StringComparison.Ordinal) ||
        string.Equals(name, EverythingName, StringComparison.Ordinal);
}
