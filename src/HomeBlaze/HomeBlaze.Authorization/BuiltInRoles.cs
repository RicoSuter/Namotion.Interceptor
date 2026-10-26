using System.Collections.Immutable;
using Namotion.Interceptor.Authorization;

namespace HomeBlaze.Authorization;

/// <summary>
/// The roles defined in code, always available and not editable. Their names are reserved for custom roles.
/// </summary>
public static class BuiltInRoles
{
    /// <summary>The name of <see cref="Reader"/>.</summary>
    public const string ReaderName = "Reader";

    /// <summary>The name of <see cref="Operator"/>.</summary>
    public const string OperatorName = "Operator";

    /// <summary>The name of <see cref="Everything"/>.</summary>
    public const string EverythingName = "Everything";

    /// <summary>
    /// Reads state, configuration, subjects, attributes, metadata and methods, reads the history of state and executes
    /// queries. Secrets stay unreadable through the system rules.
    /// </summary>
    public static AuthorizationRole Reader { get; } = new()
    {
        Name = ReaderName,
        // A rule's tags must all match, so each tag a member may carry instead of the others has its own rule.
        Allow =
        [
            new PermissionRule { Tags = [MemberTags.State], Actions = [PermissionAction.Read, AuthorizationActions.History] },
            new PermissionRule { Tags = [MemberTags.Configuration], Actions = [PermissionAction.Read] },
            new PermissionRule { Tags = [MemberTags.Subjects], Actions = [PermissionAction.Read] },
            new PermissionRule { Tags = [MemberTags.Attribute], Actions = [PermissionAction.Read] },
            new PermissionRule { Tags = [MemberTags.Metadata], Actions = [PermissionAction.Read] },
            new PermissionRule { Tags = [MemberTags.Query], Actions = [PermissionAction.Read, AuthorizationActions.Execute] },
            new PermissionRule { Tags = [MemberTags.Operation], Actions = [PermissionAction.Read] }
        ]
    };

    /// <summary>
    /// Includes <see cref="Reader"/>, executes operations and writes state and metadata.
    /// </summary>
    public static AuthorizationRole Operator { get; } = new()
    {
        Name = OperatorName,
        Includes = [ReaderName],
        Allow =
        [
            new PermissionRule { Tags = [MemberTags.Operation], Actions = [AuthorizationActions.Execute] },
            new PermissionRule { Tags = [MemberTags.State], Actions = [PermissionAction.Write] },
            new PermissionRule { Tags = [MemberTags.Metadata], Actions = [PermissionAction.Write] }
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

    /// <summary>Gets whether the name is the name of a built-in role, ignoring case.</summary>
    public static bool IsBuiltIn(string name) =>
        string.Equals(name, ReaderName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, OperatorName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, EverythingName, StringComparison.OrdinalIgnoreCase);
}
