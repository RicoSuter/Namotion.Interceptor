using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Authorization.Internal;
using HomeBlaze.Services;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Authorization;

/// <summary>
/// A custom role of an <see cref="Authorization"/>: rules that allow, rules that deny, and the roles it includes.
/// Its name is its key in <see cref="Authorization.Roles"/>, see <see cref="Title"/>.
/// </summary>
[Description("Custom role with the rules it allows and denies")]
[DisplayName("Role")]
[SubjectIcon("Badge")]
[ExcludeFromCreation]
[InterceptorSubject]
public partial class Role : IConfigurable, ITitleProvider, IIconProvider
{
    /// <summary>
    /// The names of the built-in or custom roles whose rules this role includes.
    /// </summary>
    [Configuration]
    public partial List<string> Includes { get; set; }

    /// <summary>
    /// The rules whose matches are allowed.
    /// </summary>
    [Configuration]
    public partial List<PermissionRule> Allow { get; set; }

    /// <summary>
    /// The rules whose matches are denied, whichever role allowed them.
    /// </summary>
    [Configuration]
    public partial List<PermissionRule> Deny { get; set; }

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
    /// Moves this role with its rules to the key <paramref name="name"/>, trimmed, in a new <see cref="Authorization.Roles"/>
    /// dictionary, and writes the configuration. A name that differs only in case renames it as well; the same name
    /// changes nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No <see cref="Authorization"/> holds the role; the name is empty, cannot name a custom role or names another role,
    /// ignoring case; two other roles have the same name, ignoring case; or the configuration was not or could not be
    /// written, in which case the role stays renamed.
    /// </exception>
    [Operation(Title = "Rename", Description = "Gives the role another name and keeps its rules. Agents and roles that name it by its old name report it as unknown until they are given the new name. Agents and roles that name the new name already get its rules.", Icon = "DriveFileRenameOutline", RequiresConfirmation = true)]
    public Task RenameAsync(string name, [FromServices] AuthorizationService authorizationService, [FromServices] RootManager rootManager) =>
        RoleEdits.RenameAsync(this, name, authorizationService, rootManager);

    /// <summary>
    /// Leaves this role out of a new <see cref="Authorization.Roles"/> dictionary and writes the configuration.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No <see cref="Authorization"/> holds the role; two other roles have the same name, ignoring case; or the
    /// configuration was not or could not be written, in which case the role stays deleted.
    /// </exception>
    [Operation(Title = "Delete", Description = "Deletes the role. Agents and roles that name it report it as unknown afterwards.", Icon = "Delete", RequiresConfirmation = true)]
    public Task DeleteAsync([FromServices] AuthorizationService authorizationService, [FromServices] RootManager rootManager) =>
        RoleEdits.DeleteAsync(this, authorizationService, rootManager);

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
