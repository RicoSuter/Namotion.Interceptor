using HomeBlaze.Authorization;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>
/// The <see cref="IUserAdministration"/> of a circuit: every call acts as the person signed in at that moment, so that a
/// circuit signed out meanwhile, by the person or by revalidation, refuses every change.
/// </summary>
internal sealed class CurrentUserAdministration(UserStore userStore, CurrentUser currentUser) : IUserAdministration
{
    public Task<UserChangeResult> AddUserAsync(string userName, string? email, IReadOnlyCollection<string> roles, CancellationToken cancellationToken) =>
        Current.AddUserAsync(userName, email, roles, cancellationToken);

    public Task<UserChangeResult> RemoveAsync(string userName, CancellationToken cancellationToken) =>
        Current.RemoveAsync(userName, cancellationToken);

    public Task<UserChangeResult> ResetPasswordAsync(string userName, CancellationToken cancellationToken) =>
        Current.ResetPasswordAsync(userName, cancellationToken);

    public Task<UserChangeResult> SetLockedAsync(string userName, bool isLocked, CancellationToken cancellationToken) =>
        Current.SetLockedAsync(userName, isLocked, cancellationToken);

    public Task<UserChangeResult> SetRolesAsync(string userName, IReadOnlyCollection<string> roles, CancellationToken cancellationToken) =>
        Current.SetRolesAsync(userName, roles, cancellationToken);

    public Task<UserChangeResult> SetDisplayNameAsync(string userName, string? displayName, CancellationToken cancellationToken) =>
        Current.SetDisplayNameAsync(userName, displayName, cancellationToken);

    public Task<UserChangeResult> CreateMcpKeyAsync(CancellationToken cancellationToken) =>
        Current.CreateMcpKeyAsync(cancellationToken);

    public Task<UserChangeResult> RevokeMcpKeyAsync(CancellationToken cancellationToken) =>
        Current.RevokeMcpKeyAsync(cancellationToken);

    private IUserAdministration Current => userStore.CreateAdministration(currentUser.GetPersonUserId());
}
