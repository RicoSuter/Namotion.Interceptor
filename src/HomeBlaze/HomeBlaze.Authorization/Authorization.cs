using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Authorization.Internal;
using HomeBlaze.Services;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Authorization;

/// <summary>
/// The custom roles of the installation, in one subject the operator creates, for example <c>Files/Authorization.json</c>.
/// Built-in roles are not listed here. Only one may exist; with several, none is used.
/// </summary>
[Category("System")]
[Description("Custom roles that grant agents and MCP clients access to the subjects")]
[DisplayName("Authorization")]
[SubjectIcon("AdminPanelSettings")]
[SingleInstance]
[InterceptorSubject]
public partial class Authorization : IConfigurable, IIconProvider
{
    /// <summary>
    /// The custom roles by name, each reachable at the path of this subject followed by its name. Role names compare
    /// ignoring case when roles are resolved. The dictionary is replaced as a whole, never changed in place.
    /// </summary>
    [Configuration]
    [InlinePaths]
    public partial Dictionary<string, Role> Roles { get; set; }

    /// <summary>
    /// Why this subject is not used, or null.
    /// </summary>
    [State]
    public partial string? Status { get; internal set; }

    /// <inheritdoc />
    public string IconName => "AdminPanelSettings";

    public Authorization()
    {
        Roles = new Dictionary<string, Role>(StringComparer.OrdinalIgnoreCase);
        Status = null;
    }

    /// <summary>
    /// Adds a custom role without rules under <paramref name="name"/>, trimmed, in a new <see cref="Roles"/> dictionary,
    /// and writes the configuration.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The name is empty, cannot name a custom role (see <see cref="AuthorizationService.GetRoleNameError"/>) or names a
    /// role already, ignoring case; two roles have the same name, ignoring case; or the configuration was not written, in
    /// which case the role stays added.
    /// </exception>
    [Operation(Title = "Add Role", Description = "Adds a custom role without rules under the given name. Its rules are edited in its own dialog.", Icon = "Add")]
    public Task AddRoleAsync(string name, [FromServices] AuthorizationService authorizationService, [FromServices] RootManager rootManager) =>
        RoleEdits.AddAsync(this, name, authorizationService, rootManager);

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
