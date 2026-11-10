using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Authorization.Internal;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Authorization;

/// <summary>
/// A built-in or custom role of an <see cref="Authorization"/>: rules that allow, rules that deny, and the roles it
/// includes. Its name is its key in <see cref="Authorization.Roles"/>, see <see cref="Title"/>. The rules of a custom role
/// change only through the <see cref="IRoleStore"/>, which replaces every role; a built-in role never changes.
/// </summary>
[Description("Role with the rules it allows and denies")]
[DisplayName("Role")]
[SubjectIcon("Badge")]
[ExcludeFromCreation]
[InterceptorSubject]
public partial class Role : ITitleProvider, IIconProvider
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

    /// <summary>Whether this is a built-in role, see <see cref="BuiltInRoles"/>, which cannot be edited, renamed or deleted.</summary>
    [State]
    public partial bool IsBuiltIn { get; internal set; }

    /// <summary>What the role allows, for a built-in role, or null for a custom role.</summary>
    [State]
    public partial string? Description { get; internal set; }

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

    /// <summary>Whether <see cref="RenameAsync"/> applies: not for a built-in role.</summary>
    [Derived]
    [PropertyAttribute("Rename", KnownAttributes.IsEnabled)]
    public bool IsRenameEnabled => !IsBuiltIn;

    /// <summary>Why <see cref="RenameAsync"/> does not apply, or null.</summary>
    [Derived]
    [PropertyAttribute("Rename", KnownAttributes.DisabledReason)]
    public string? RenameDisabledReason => IsBuiltIn ? BuiltInRoleReason : null;

    /// <summary>Whether <see cref="DeleteAsync"/> applies: not for a built-in role.</summary>
    [Derived]
    [PropertyAttribute("Delete", KnownAttributes.IsEnabled)]
    public bool IsDeleteEnabled => !IsBuiltIn;

    /// <summary>Why <see cref="DeleteAsync"/> does not apply, or null.</summary>
    [Derived]
    [PropertyAttribute("Delete", KnownAttributes.DisabledReason)]
    public string? DeleteDisabledReason => IsBuiltIn ? BuiltInRoleReason : null;

    /// <summary>Whether <see cref="EditAsync"/> applies: not for a built-in role.</summary>
    [Derived]
    [PropertyAttribute("Edit", KnownAttributes.IsEnabled)]
    public bool IsEditEnabled => !IsBuiltIn;

    /// <summary>Why <see cref="EditAsync"/> does not apply, or null.</summary>
    [Derived]
    [PropertyAttribute("Edit", KnownAttributes.DisabledReason)]
    public string? EditDisabledReason => IsBuiltIn ? BuiltInRoleReason : null;

    internal const string BuiltInRoleReason = "Built-in roles cannot be edited, renamed or deleted.";

    public Role()
    {
        Includes = [];
        Allow = [];
        Deny = [];
        IsBuiltIn = false;
        Description = null;
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

    /// <summary>
    /// Saves the roles this role includes and its rules through the <see cref="IRoleStore"/>, with the stamp of the rules
    /// this subject holds; the edit dialog of the role saves through this operation.
    /// </summary>
    /// <param name="includes">The names of the roles whose rules the role includes.</param>
    /// <param name="allow">The rules whose matches the role allows.</param>
    /// <param name="deny">The rules whose matches the role denies.</param>
    /// <param name="roleStore">The store that saves the role.</param>
    /// <param name="cancellationToken">Cancels the save until it is committed.</param>
    /// <exception cref="InvalidOperationException">
    /// The role is built in, no <see cref="Authorization"/> holds this subject, because the role was changed, renamed or
    /// deleted since, or the store refused the save.
    /// </exception>
    [Operation(Title = "Edit", Description = "Saves the roles this role includes and its allow and deny rules.", Icon = "Edit", RequiresAdministration = true, Purpose = OperationPurpose.Edit)]
    public Task EditAsync(
        IReadOnlyList<string> includes,
        IReadOnlyList<PermissionRule> allow,
        IReadOnlyList<PermissionRule> deny,
        [FromServices] IRoleStore roleStore,
        CancellationToken cancellationToken) =>
        RoleEdits.EditAsync(this, includes, allow, deny, roleStore, cancellationToken);
}
