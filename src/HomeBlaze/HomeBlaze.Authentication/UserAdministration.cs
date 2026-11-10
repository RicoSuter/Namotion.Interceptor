using HomeBlaze.Authentication.Internal;
using HomeBlaze.Authorization;

namespace HomeBlaze.Authentication;

/// <summary>
/// The user administration of the person whose user id <c>getActingUserId</c> gives at the time of each call, whose
/// changes the <see cref="UserStore"/> checks and commits; while it gives null, every change is refused. Thread-safe.
/// </summary>
public sealed class UserAdministration : IUserAdministration
{
    private static readonly Task<UserChangeResult> ActorRefusal = Task.FromResult(new UserChangeResult(false, UserStore.ActorRefusal));

    private readonly UserStore _store;
    private readonly Func<string?> _getActingUserId;

    /// <param name="store">Checks and commits the changes.</param>
    /// <param name="getActingUserId">Gets the user id of the acting person, or null when nobody may act.</param>
    public UserAdministration(UserStore store, Func<string?> getActingUserId)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(getActingUserId);

        _store = store;
        _getActingUserId = getActingUserId;
    }

    /// <inheritdoc />
    public Task<UserChangeResult> AddUserAsync(string userName, string? email, IReadOnlyCollection<string> roles, CancellationToken cancellationToken) =>
        _getActingUserId() is { } actingUserId ? _store.AddUserAsync(actingUserId, userName, email, roles, cancellationToken) : ActorRefusal;

    /// <inheritdoc />
    public Task<UserChangeResult> RemoveAsync(string userName, CancellationToken cancellationToken) =>
        ChangeUserAsync(userName, new UserChange(UserAction.Remove), cancellationToken);

    /// <inheritdoc />
    public Task<UserChangeResult> ResetPasswordAsync(string userName, CancellationToken cancellationToken) =>
        ChangeUserAsync(userName, new UserChange(UserAction.Reset), cancellationToken);

    /// <inheritdoc />
    public Task<UserChangeResult> SetLockedAsync(string userName, bool isLocked, CancellationToken cancellationToken) =>
        ChangeUserAsync(userName, new UserChange(isLocked ? UserAction.Lock : UserAction.Unlock), cancellationToken);

    /// <inheritdoc />
    public Task<UserChangeResult> SetRolesAsync(string userName, IReadOnlyCollection<string> roles, CancellationToken cancellationToken) =>
        ChangeUserAsync(userName, UserChange.SetRoles(roles), cancellationToken);

    /// <inheritdoc />
    public Task<UserChangeResult> SetDisplayNameAsync(string userName, string? displayName, CancellationToken cancellationToken) =>
        ChangeUserAsync(userName, UserChange.SetDisplayName(displayName), cancellationToken);

    /// <inheritdoc />
    public Task<UserChangeResult> CreateMcpKeyAsync(CancellationToken cancellationToken) =>
        _getActingUserId() is { } actingUserId ? _store.SetMcpKeyAsync(actingUserId, create: true, cancellationToken) : ActorRefusal;

    /// <inheritdoc />
    public Task<UserChangeResult> RevokeMcpKeyAsync(CancellationToken cancellationToken) =>
        _getActingUserId() is { } actingUserId ? _store.SetMcpKeyAsync(actingUserId, create: false, cancellationToken) : ActorRefusal;

    private Task<UserChangeResult> ChangeUserAsync(string userName, UserChange change, CancellationToken cancellationToken) =>
        _getActingUserId() is { } actingUserId ? _store.ChangeUserAsync(actingUserId, userName, change, cancellationToken) : ActorRefusal;
}
