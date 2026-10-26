using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Authorization;

namespace HomeBlaze.Authorization;

/// <summary>
/// A custom role of an <see cref="Authorization"/>: rules that allow, rules that deny, and the roles it includes.
/// Its name is its key in <see cref="Authorization.Roles"/>.
/// </summary>
[Description("Custom role with the rules it allows and denies")]
[DisplayName("Role")]
[SubjectIcon("Badge")]
[ExcludeFromCreation]
[InterceptorSubject]
public partial class Role : IConfigurable
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
    /// The number of agents whose roles name this role.
    /// </summary>
    [State]
    public partial int UsedByAgents { get; internal set; }

    public Role()
    {
        Includes = [];
        Allow = [];
        Deny = [];
        Status = null;
        UsedByAgents = 0;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
