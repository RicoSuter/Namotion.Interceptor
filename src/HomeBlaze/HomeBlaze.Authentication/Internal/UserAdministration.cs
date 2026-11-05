using HomeBlaze.Authorization;

namespace HomeBlaze.Authentication.Internal;

/// <summary>The administration of one acting person, whose changes the user store checks and commits.</summary>
internal sealed class UserAdministration(UserStore store, string actingUserId) : IUserAdministration
{
    /// <summary>Creates the administration of the person with <paramref name="actingUserId"/>; null gives one that refuses every change.</summary>
    public static IUserAdministration Create(UserStore store, string? actingUserId) =>
        actingUserId is null ? RefusingUserAdministration.Instance : new UserAdministration(store, actingUserId);

    public Task<UserChangeResult> AddUserAsync(string userName, string? email, IReadOnlyCollection<string> roles, CancellationToken cancellationToken) =>
        store.AddUserAsync(actingUserId, userName, email, roles, cancellationToken);

    public Task<UserChangeResult> RemoveAsync(string userName, CancellationToken cancellationToken) =>
        store.ChangeUserAsync(actingUserId, userName, new UserChange(UserAction.Remove), cancellationToken);

    public Task<UserChangeResult> ResetPasswordAsync(string userName, CancellationToken cancellationToken) =>
        store.ChangeUserAsync(actingUserId, userName, new UserChange(UserAction.Reset), cancellationToken);

    public Task<UserChangeResult> SetLockedAsync(string userName, bool isLocked, CancellationToken cancellationToken) =>
        store.ChangeUserAsync(actingUserId, userName, new UserChange(isLocked ? UserAction.Lock : UserAction.Unlock), cancellationToken);

    public Task<UserChangeResult> SetRolesAsync(string userName, IReadOnlyCollection<string> roles, CancellationToken cancellationToken) =>
        store.ChangeUserAsync(actingUserId, userName, UserChange.SetRoles(roles), cancellationToken);

    public Task<UserChangeResult> SetDisplayNameAsync(string userName, string? displayName, CancellationToken cancellationToken) =>
        store.ChangeUserAsync(actingUserId, userName, UserChange.SetDisplayName(displayName), cancellationToken);

    public Task<UserChangeResult> CreateMcpKeyAsync(CancellationToken cancellationToken) =>
        store.SetMcpKeyAsync(actingUserId, create: true, cancellationToken);

    public Task<UserChangeResult> RevokeMcpKeyAsync(CancellationToken cancellationToken) =>
        store.SetMcpKeyAsync(actingUserId, create: false, cancellationToken);
}

/// <summary>The administration when nobody is signed in: refuses every change.</summary>
internal sealed class RefusingUserAdministration : IUserAdministration
{
    public static readonly RefusingUserAdministration Instance = new();

    private static readonly Task<UserChangeResult> Refusal = Task.FromResult(new UserChangeResult(false, UserStore.ActorRefusal));

    public Task<UserChangeResult> AddUserAsync(string userName, string? email, IReadOnlyCollection<string> roles, CancellationToken cancellationToken) => Refusal;

    public Task<UserChangeResult> RemoveAsync(string userName, CancellationToken cancellationToken) => Refusal;

    public Task<UserChangeResult> ResetPasswordAsync(string userName, CancellationToken cancellationToken) => Refusal;

    public Task<UserChangeResult> SetLockedAsync(string userName, bool isLocked, CancellationToken cancellationToken) => Refusal;

    public Task<UserChangeResult> SetRolesAsync(string userName, IReadOnlyCollection<string> roles, CancellationToken cancellationToken) => Refusal;

    public Task<UserChangeResult> SetDisplayNameAsync(string userName, string? displayName, CancellationToken cancellationToken) => Refusal;

    public Task<UserChangeResult> CreateMcpKeyAsync(CancellationToken cancellationToken) => Refusal;

    public Task<UserChangeResult> RevokeMcpKeyAsync(CancellationToken cancellationToken) => Refusal;
}
