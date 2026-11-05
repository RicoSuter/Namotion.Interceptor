using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Authorization.Internal;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Authorization;

/// <summary>
/// A custom role of an <see cref="Authorization"/>: rules that allow, rules that deny, and the roles it includes.
/// Its name is its key in <see cref="Authorization.Roles"/>, see <see cref="Title"/>. Its rules change only through the
/// <see cref="IRoleStore"/>, which replaces every role.
/// </summary>
[Description("Custom role with the rules it allows and denies")]
[DisplayName("Role")]
[SubjectIcon("Badge")]
[ExcludeFromCreation]
[InterceptorSubject]
public partial class Role : ISubjectEditPermission, ITitleProvider, IIconProvider
{
    /// <summary>
    /// The names of the built-in or custom roles whose rules this role includes.
    /// </summary>
    [State]
    public partial IReadOnlyList<string> Includes { get; internal set; }

    /// <summary>
    /// The rules whose matches are allowed.
    /// </summary>
    [State]
    public partial IReadOnlyList<PermissionRule> Allow { get; internal set; }

    /// <summary>
    /// The rules whose matches are denied, whichever role allowed them.
    /// </summary>
    [State]
    public partial IReadOnlyList<PermissionRule> Deny { get; internal set; }

    /// <summary>
    /// Why the role cannot be used, such as a compile error or a reserved name, or null.
    /// </summary>
    [State]
    public partial string? Status { get; internal set; }

    /// <summary>
    /// Hints about rules that likely do not do what they say, such as a path pattern that names a subject instead of a
    /// member, or null. The role stays usable.
    /// </summary>
    [State]
    public partial string? Warning { get; internal set; }

    /// <summary>
    /// The number of agents whose roles name this role.
    /// </summary>
    [State]
    public partial int UsedByAgents { get; internal set; }

    /// <summary>
    /// The name of the role, its key in <see cref="Authorization.Roles"/>, or null while no <see cref="Authorization"/> holds it.
    /// </summary>
    public string? Title => FindParent().Name;

    /// <inheritdoc />
    public string IconName => "Badge";

    public Role()
    {
        Includes = [];
        Allow = [];
        Deny = [];
        Status = null;
        Warning = null;
        UsedByAgents = 0;
    }

    /// <summary>
    /// Gets the <see cref="Authorization"/> that holds this role and the key it holds it under, or nulls while none does.
    /// </summary>
    public (Authorization? Authorization, string? Name) FindParent()
    {
        var registeredRole = this.TryGetRegisteredSubject();
        if (registeredRole is null)
        {
            return (null, null);
        }

        foreach (var parent in registeredRole.Parents)
        {
            if (parent.Property.Subject is Authorization authorization && parent.Index is string name)
            {
                return (authorization, name);
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Renames this role to <paramref name="name"/>, trimmed, through the <see cref="IRoleStore"/>, which keeps its rules
    /// and renames it in the includes of the other roles, in the users and in the agents. The same name changes nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No <see cref="Authorization"/> holds the role, the name is empty, or the store refused the rename, such as for a
    /// name that cannot name a custom role or names another role. Also thrown with the warning when the
    /// rename was committed but could not be saved everywhere.
    /// </exception>
    [Operation(Title = "Rename", Description = "Gives the role another name and keeps its rules. The roles, users and agents that name it are given the new name as well.", Icon = "DriveFileRenameOutline", RequiresConfirmation = true, RequiresAdministration = true)]
    public Task RenameAsync(string name, [FromServices] IRoleStore roleStore, CancellationToken cancellationToken) =>
        RoleEdits.RenameAsync(this, name, roleStore, cancellationToken);

    /// <summary>
    /// Deletes this role through the <see cref="IRoleStore"/>, which refuses while a user or an agent names it or a
    /// principal would lose its permissions.
    /// </summary>
    /// <exception cref="InvalidOperationException">No <see cref="Authorization"/> holds the role, or the store refused the delete.</exception>
    [Operation(Title = "Delete", Description = "Deletes the role. Refused while users or agents hold it, or while other roles that are in use include it.", Icon = "Delete", RequiresConfirmation = true, RequiresAdministration = true, Purpose = OperationPurpose.Delete)]
    public Task DeleteAsync([FromServices] IRoleStore roleStore, CancellationToken cancellationToken) =>
        RoleEdits.DeleteAsync(this, roleStore, cancellationToken);

    /// <summary>The editor changes the rules of the role, which replaces <see cref="Authorization.Roles"/> that holds it.</summary>
    bool ISubjectEditPermission.CanEdit(IUserPermissions permissions) => permissions.CanAdministerHoldingMembers(this);
}
