using System.ComponentModel;
using System.Globalization;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Authorization.Internal;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Authorization;

/// <summary>
/// The roles and the users of the installation, in one subject the operator creates, for example
/// <c>Files/Authorization.json</c>. Only one may exist; with several, none is used.
/// </summary>
[Category("System")]
[Description("Roles and users that grant people, agents and MCP clients access to the subjects")]
[DisplayName("Authorization")]
[SubjectIcon("AdminPanelSettings")]
[SingleInstance]
[InterceptorSubject]
public partial class Authorization : IConfigurable, IIconProvider
{
    /// <summary>
    /// The built-in roles, then the custom roles, by name, each reachable at the path of this subject followed by its name.
    /// The custom roles are loaded from the authentication database and never saved with this subject; replaced as a
    /// whole by <see cref="ReplaceRoles"/>.
    /// </summary>
    [State]
    [InlinePaths]
    public partial IReadOnlyDictionary<string, Role> Roles { get; internal set; }

    /// <summary>
    /// The users by user name, including the built-in users. Loaded from the authentication database and
    /// never saved with this subject; replaced as a whole by <see cref="ReplaceUsers"/>.
    /// </summary>
    [State]
    public partial IReadOnlyDictionary<string, User> Users { get; internal set; }

    /// <summary>
    /// Why this subject is not used, or null.
    /// </summary>
    [State]
    public partial string? Status { get; internal set; }

    /// <inheritdoc />
    public string IconName => "AdminPanelSettings";

    public Authorization()
    {
        Roles = CreateRoles([]);
        Users = new Dictionary<string, User>(StringComparer.Ordinal);
        Status = null;
    }

    /// <summary>
    /// Replaces the roles with the built-in roles followed by the custom <paramref name="roles"/>, building a
    /// <see cref="Role"/> per entry, in one assignment. Called by the role store after a committed change; nothing else
    /// changes the roles.
    /// </summary>
    public void ReplaceRoles(IReadOnlyCollection<AuthorizationRole> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        Roles = CreateRoles(roles);
    }

    // A custom role with the name of a built-in role, which the role store refuses, never replaces the built-in entry.
    private static Dictionary<string, Role> CreateRoles(IReadOnlyCollection<AuthorizationRole> customRoles)
    {
        var roles = new Dictionary<string, Role>(BuiltInRoles.All.Length + customRoles.Count, StringComparer.Ordinal);
        foreach (var role in BuiltInRoles.All)
        {
            roles[role.Name] = new Role
            {
                Includes = [.. role.Includes],
                Allow = [.. role.Allow],
                Deny = [.. role.Deny],
                IsBuiltIn = true,
                Description = BuiltInRoles.GetDescription(role.Name)
            };
        }

        foreach (var role in customRoles)
        {
            roles.TryAdd(role.Name, new Role { Includes = [.. role.Includes], Allow = [.. role.Allow], Deny = [.. role.Deny] });
        }

        return roles;
    }

    /// <summary>
    /// Replaces the users with <paramref name="users"/>, building a <see cref="User"/> per entry, in one assignment.
    /// Called by the user store after a committed change; nothing else changes the users.
    /// </summary>
    public void ReplaceUsers(IReadOnlyCollection<UserSnapshot> users)
    {
        ArgumentNullException.ThrowIfNull(users);

        var replaced = new Dictionary<string, User>(users.Count, StringComparer.Ordinal);
        foreach (var user in users)
        {
            replaced[user.UserName] = new User
            {
                UserName = user.UserName,
                DisplayName = user.DisplayName,
                Email = user.Email,
                Roles = [.. user.Roles],
                IsLockedOut = user.IsLockedOut,
                MustChangePassword = user.MustChangePassword,
                LastLogin = user.LastLogin,
                IsBuiltIn = user.IsBuiltIn,
                HasKey = user.HasKey,
                Warning = User.GetWarning(user)
            };
        }

        Users = replaced;
    }

    /// <summary>
    /// Adds a custom role without rules under <paramref name="name"/>, trimmed, through the <see cref="IRoleStore"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The Authorization is not in the graph, the name is empty, or the store refused the role, such as for a name that
    /// cannot name a custom role (see <see cref="AuthorizationService.GetRoleNameError"/>) or names a role already.
    /// </exception>
    [Operation(Title = "Add Role", Description = "Adds a custom role without rules under the given name. Its rules are edited in its own dialog. Agents and roles that already name it get its rules.", Icon = "Add", RequiresAdministration = true)]
    public Task AddRoleAsync(string name, [FromServices] IRoleStore roleStore, CancellationToken cancellationToken) =>
        RoleEdits.AddAsync(this, name, roleStore, cancellationToken);

    /// <summary>Adds a user without a password, holding <paramref name="roles"/>, and returns their one-time setup code.</summary>
    [Operation(Title = "Add User", Description = "Adds a user without a password. They set one with a one-time setup code, shown next. Only the roles you hold can be assigned.", Icon = "PersonAdd", RequiresAdministration = true)]
    public async Task<OneTimeSecret> AddUserAsync(
        [FromServices] IUserAdministration administration,
        string userName,
        string? email,
        [OperationParameter(Editor = AuthorizationParameterEditors.RoleNames)] string[] roles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(administration);
        ArgumentNullException.ThrowIfNull(roles);

        var result = await administration.AddUserAsync(userName, email, roles, cancellationToken);
        User.ThrowIfRefused(result);
        return CreateSetupCodeSecret(userName.Trim(), result.SetupCode!);
    }

    /// <summary>The page on which a user redeems a setup code, relative to the base address of the application.</summary>
    internal const string SetupPagePath = "account/setup";

    internal static OneTimeSecret CreateSetupCodeSecret(string userName, string setupCode) =>
        new(
            $"Setup code for {userName}",
            string.Create(CultureInfo.InvariantCulture,
                $"Give {userName} this setup code, which is shown only now. It is valid for {UserChangeResult.SetupCodeLifetime.TotalHours:0} hours and works once: they enter it with their user name on the setup page and then choose a password."),
            setupCode,
            SetupPagePath);

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
