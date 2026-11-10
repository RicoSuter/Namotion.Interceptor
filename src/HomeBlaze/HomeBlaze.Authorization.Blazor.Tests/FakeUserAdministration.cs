namespace HomeBlaze.Authorization.Blazor.Tests;

/// <summary>
/// A user administration that records every change and returns <see cref="Error"/> as a refusal, or else succeeds with
/// <see cref="SetupCode"/> for an added or reset user and <see cref="McpKey"/> for a created key; a successful change is
/// applied to the users of the authorization with <see cref="Authorization.ReplaceUsers"/>, keeping their order.
/// </summary>
internal sealed class FakeUserAdministration(Authorization target) : IUserAdministration
{
    /// <summary>Why every change is refused, or null to let it succeed.</summary>
    public string? Error { get; set; }

    /// <summary>Thrown by every change when set, as a store does that fails unexpectedly.</summary>
    public Exception? Exception { get; set; }

    public string SetupCode { get; set; } = "SETUP-1234";

    public string McpKey { get; set; } = "KEY-5678";

    /// <summary>Each change as the name of its method and its arguments, such as <c>SetRoles alice Reader,Kitchen</c>.</summary>
    public List<string> Calls { get; } = [];

    public Task<UserChangeResult> AddUserAsync(string userName, string? email, IReadOnlyCollection<string> roles, CancellationToken cancellationToken) =>
        Change($"AddUser {userName} {email} {string.Join(',', roles)}", users => users.Add(new UserSnapshot(userName, email, [.. roles], false, true, null, false, false)), SetupCode);

    public Task<UserChangeResult> RemoveAsync(string userName, CancellationToken cancellationToken) =>
        Change($"Remove {userName}", users => users.RemoveAll(user => user.UserName == userName));

    public Task<UserChangeResult> ResetPasswordAsync(string userName, CancellationToken cancellationToken) =>
        Change($"ResetPassword {userName}", users => Update(users, userName, user => user with { MustChangePassword = true }), SetupCode);

    public Task<UserChangeResult> SetLockedAsync(string userName, bool isLocked, CancellationToken cancellationToken) =>
        Change($"SetLocked {userName} {isLocked}", users => Update(users, userName, user => user with { IsLockedOut = isLocked }));

    public Task<UserChangeResult> SetRolesAsync(string userName, IReadOnlyCollection<string> roles, CancellationToken cancellationToken) =>
        Change($"SetRoles {userName} {string.Join(',', roles)}", users => Update(users, userName, user => user with { Roles = [.. roles] }));

    public Task<UserChangeResult> SetDisplayNameAsync(string userName, string? displayName, CancellationToken cancellationToken) =>
        Change($"SetDisplayName {userName} {displayName}", users => Update(users, userName, user => user with { DisplayName = displayName }));

    public Task<UserChangeResult> CreateMcpKeyAsync(CancellationToken cancellationToken) =>
        Change("CreateMcpKey", users => Update(users, NameRules.McpUserName, user => user with { HasKey = true }), mcpKey: McpKey);

    public Task<UserChangeResult> RevokeMcpKeyAsync(CancellationToken cancellationToken) =>
        Change("RevokeMcpKey", users => Update(users, NameRules.McpUserName, user => user with { HasKey = false }));

    private Task<UserChangeResult> Change(string call, Action<List<UserSnapshot>> apply, string? setupCode = null, string? mcpKey = null)
    {
        Calls.Add(call);
        if (Exception is not null)
        {
            return Task.FromException<UserChangeResult>(Exception);
        }

        if (Error is not null)
        {
            return Task.FromResult(new UserChangeResult(false, Error));
        }

        var users = target.Users.Values
            .Select(user => new UserSnapshot(user.UserName, user.Email, user.Roles, user.IsLockedOut, user.MustChangePassword, user.LastLogin, user.IsBuiltIn, user.HasKey, user.DisplayName))
            .ToList();
        apply(users);
        target.ReplaceUsers(users);
        return Task.FromResult(new UserChangeResult(true, null, setupCode, mcpKey));
    }

    private static void Update(List<UserSnapshot> users, string userName, Func<UserSnapshot, UserSnapshot> update)
    {
        var index = users.FindIndex(user => string.Equals(user.UserName, userName, StringComparison.Ordinal));
        users[index] = update(users[index]);
    }
}
