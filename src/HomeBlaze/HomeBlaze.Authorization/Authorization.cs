using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
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
public partial class Authorization : IConfigurable
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

    public Authorization()
    {
        Roles = new Dictionary<string, Role>(StringComparer.OrdinalIgnoreCase);
        Status = null;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
