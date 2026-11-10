using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using HomeBlaze.Authentication.Internal;
using HomeBlaze.Authorization;
using Microsoft.Extensions.DependencyInjection;
using AuthorizationSubject = HomeBlaze.Authorization.Authorization;

namespace HomeBlaze.Authentication;

/// <summary>
/// Stores the users, including the built-in users, in the authentication database and projects them into
/// <see cref="AuthorizationSubject.Users"/>. Users change only through a <see cref="UserAdministration"/>; a change runs
/// one at a time with the changes of roles, through the <see cref="AuthorizationChangeGate"/>, and is committed before
/// the projection changes. As <see cref="IMcpClientAccess"/>, gives the roles of the built-in user
/// <see cref="NameRules.McpUserName"/>; checks its key against the hash held in memory, refreshed on every load and change
/// of the key. Thread-safe.
/// </summary>
public sealed class UserStore : IMcpClientAccess
{
    internal const string ActorRefusal = "Only a signed-in person with an active account can manage users.";

    internal const string OwnAccountRefusal = "Only a signed-in person with an active account can change their account.";

    /// <summary>How long after a reload of a user that <see cref="RequestStampReload"/> started it starts no other.</summary>
    public static readonly TimeSpan StampReloadInterval = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AuthorizationProjection _projection;
    private readonly AuthorizationChangeGate _gate;
    private readonly TimeProvider _timeProvider;
    private readonly UserChangeRules _rules;

    // The timestamp of the latest reload requested by RequestStampReload, by user id.
    private readonly ConcurrentDictionary<string, long> _stampReloadTimestamps = new(StringComparer.Ordinal);

    // Replaced as a whole under _gate, never changed in place, so it is read without the gate.
    private volatile UserState _state = UserState.Empty;

    /// <param name="scopeFactory">Creates a scope per operation for Identity's scoped user manager and the database context.</param>
    /// <param name="authorizationService">Compiles the policy of the acting person and the closures of roles.</param>
    /// <param name="roleStore">Knows the custom roles that may be assigned, and reports their renames.</param>
    /// <param name="projection">Shows the stored users in the graph.</param>
    /// <param name="gate">Runs the changes of roles and users one at a time.</param>
    /// <param name="timeProvider">Gives the time of lockouts and of setup codes.</param>
    internal UserStore(
        IServiceScopeFactory scopeFactory,
        AuthorizationService authorizationService,
        RoleStore roleStore,
        AuthorizationProjection projection,
        AuthorizationChangeGate gate,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(authorizationService);
        ArgumentNullException.ThrowIfNull(roleStore);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _scopeFactory = scopeFactory;
        _projection = projection;
        _gate = gate;
        _timeProvider = timeProvider;
        _rules = new UserChangeRules(authorizationService, roleStore, projection);
        roleStore.RoleRenamed += OnRoleRenamed;
    }

    /// <summary>Logs every committed change.</summary>
    internal AuthenticationEventLog EventLog { get; init; } = AuthenticationEventLog.Silent;

    /// <summary>
    /// Raised with the id of a user after a change of that user is committed and projected, outside the gate. An exception
    /// of a handler is logged and skips no other handler, and never fails the change.
    /// </summary>
    public event Action<string>? UserChanged;

    /// <summary>The roles of the built-in user of visitors who are not signed in; empty when it does not exist.</summary>
    public IReadOnlyCollection<string> AnonymousRoles => GetRolesByName(NameRules.AnonymousUserName);

    /// <summary>The roles of the built-in user of the external MCP endpoint; empty when it does not exist.</summary>
    internal IReadOnlyCollection<string> McpRoles => GetRolesByName(NameRules.McpUserName);

    /// <summary>
    /// Whether the seeded administrator <see cref="AdminSeeding.AdminUserName"/> still signs in with an empty password,
    /// from seeding or a reset until they set a password.
    /// </summary>
    public bool IsSeededAdministratorUnclaimed => _state.IsSeededAdministratorUnclaimed;

    /// <inheritdoc />
    IReadOnlyCollection<string> IMcpClientAccess.Roles => McpRoles;

    /// <summary>Whether the built-in user <see cref="NameRules.McpUserName"/> has a key, which clients must then present.</summary>
    public bool RequiresKey => _state.McpKeyHash is not null;

    /// <summary>Whether <paramref name="presentedKey"/> is the key of the built-in user <see cref="NameRules.McpUserName"/>, compared in constant time; false when there is no key.</summary>
    public bool Verify(string presentedKey)
    {
        ArgumentNullException.ThrowIfNull(presentedKey);

        return McpClientKey.Matches(_state.McpKeyHash, presentedKey);
    }

    /// <summary>Loads the users with their role names from the database and projects them.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Publish(await UserDatabase.LoadAsync(_scopeFactory, cancellationToken));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Gets the role names that the user with <paramref name="userId"/> acts with; empty when there is no such user or an
    /// administrator locked them.
    /// </summary>
    public IReadOnlyCollection<string> GetRoles(string userId)
    {
        ArgumentNullException.ThrowIfNull(userId);

        return _state.ById.TryGetValue(userId, out var user) && !user.IsLockedByAdministrator ? user.Roles : [];
    }

    /// <summary>
    /// Whether a session of the user with <paramref name="userId"/> may go on, as the store knows without reading the
    /// database: the user exists, is a person, no administrator locked them, and the session carries their security stamp
    /// as stored now. A session of a person who must change their password goes on, confined by
    /// <see cref="RequiresPasswordChange"/>.
    /// </summary>
    /// <param name="userId">The id of the user of the session.</param>
    /// <param name="securityStamp">The security stamp the session carries, or null when it carries none.</param>
    public bool IsSessionAllowed(string userId, string? securityStamp)
    {
        ArgumentNullException.ThrowIfNull(userId);

        if (securityStamp is null || !_state.ById.TryGetValue(userId, out var user) || user.IsBuiltIn || user.IsLockedByAdministrator)
        {
            return false;
        }

        return string.Equals(securityStamp, user.SecurityStamp, StringComparison.Ordinal);
    }

    /// <summary>
    /// Starts reloading the user with <paramref name="userId"/> from the database in the background when a session carries
    /// <paramref name="securityStamp"/> and the store holds another stamp for a person it lets sign in, so that a stamp the
    /// store missed, such as after a reload that failed once a password change was committed, reaches it. At most one
    /// reload per user starts within <see cref="StampReloadInterval"/>. The store keeps refusing the session until the
    /// reload completed, keeps what it held when the reload fails, and raises <see cref="UserChanged"/> only when the stamp
    /// changed.
    /// </summary>
    /// <returns>The reload that started, or a completed task when none started.</returns>
    public Task RequestStampReload(string userId, string? securityStamp)
    {
        ArgumentNullException.ThrowIfNull(userId);

        if (securityStamp is null || !_state.ById.TryGetValue(userId, out var user) || !HoldsOtherStampOfPerson(user, securityStamp))
        {
            return Task.CompletedTask;
        }

        var now = _timeProvider.GetTimestamp();
        var isClaimed = _stampReloadTimestamps.TryGetValue(userId, out var last)
            ? _timeProvider.GetElapsedTime(last, now) >= StampReloadInterval && _stampReloadTimestamps.TryUpdate(userId, now, last)
            : _stampReloadTimestamps.TryAdd(userId, now);

        // Off the request: the database is read synchronously by its provider.
        return isClaimed ? Task.Run(() => ReloadStampAsync(userId)) : Task.CompletedTask;
    }

    /// <summary>
    /// Whether the person with <paramref name="userId"/> may do nothing but set a password: they must change it, as after
    /// a reset or a setup code, or sign in with an empty one. True for a user the store does not know.
    /// </summary>
    public bool RequiresPasswordChange(string userId)
    {
        ArgumentNullException.ThrowIfNull(userId);

        return !_state.ById.TryGetValue(userId, out var user) || user.RequiresPasswordChange;
    }

    /// <summary>Gets the id of the user named <paramref name="userName"/>, or null.</summary>
    public string? GetUserId(string userName)
    {
        ArgumentNullException.ThrowIfNull(userName);

        return _state.ByName.TryGetValue(userName, out var user) ? user.Id : null;
    }

    /// <summary>Gets the display name of the user with <paramref name="userId"/>, or null when they have none or do not exist.</summary>
    public string? GetDisplayName(string userId)
    {
        ArgumentNullException.ThrowIfNull(userId);

        return _state.ById.TryGetValue(userId, out var user) ? user.DisplayName : null;
    }

    /// <summary>
    /// Sets the display name of the person with <paramref name="userId"/> as they change it themselves, see
    /// <see cref="NameRules.GetDisplayNameError"/>; an empty one removes it. Refused for a user who is unknown, built in or
    /// locked by an administrator. Their sessions keep going.
    /// </summary>
    public Task<UserChangeResult> SetOwnDisplayNameAsync(string userId, string? displayName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userId);

        var change = UserChange.SetDisplayName(displayName);
        if (NameRules.GetDisplayNameError(change.DisplayName) is { } error)
        {
            return Task.FromResult(new UserChangeResult(false, error));
        }

        return RunAsync(token => SetOwnDisplayNameCoreAsync(userId, change, token), cancellationToken);
    }

    /// <summary>Gets the role names of every user, including the built-in users, each labeled with the user name.</summary>
    public IEnumerable<PrincipalRoleSet> GetAllRoleSets() => _state.RoleSets;

    /// <summary>Gets the role names of every user who is a person, not a built-in user, and whether an administrator locked them.</summary>
    internal IReadOnlyCollection<PersonRoleSet> GetPersonRoleSets() => _state.PersonRoleSets;

    /// <summary>
    /// Stores now as the last sign-in of the user with <paramref name="userId"/> and reloads what the sign-in changed, as
    /// <see cref="RefreshUserAsync"/> does. A refused write is logged, not thrown, as the person is signed in already.
    /// </summary>
    /// <remarks>Pass a token that a client disconnect does not cancel: a canceled call leaves the store without the sign-in.</remarks>
    public Task RecordSignInAsync(string userId, CancellationToken cancellationToken) =>
        RefreshUserCoreAsync(userId, _timeProvider.GetUtcNow(), onlyWhenStampChanged: false, cancellationToken);

    /// <summary>
    /// Reloads the lockout, the required password change, whether an empty password is allowed, the last sign-in and the
    /// security stamp of the user with <paramref name="userId"/>, after Identity changed them in the database, such as a
    /// lockout after failed sign-ins or a set password. Nothing changes for a user the store does not know.
    /// </summary>
    /// <remarks>
    /// Pass a token that a client disconnect does not cancel: a canceled call keeps what the store held before the write,
    /// such as the old security stamp.
    /// </remarks>
    public Task RefreshUserAsync(string userId, CancellationToken cancellationToken) =>
        RefreshUserCoreAsync(userId, signInTime: null, onlyWhenStampChanged: false, cancellationToken);

    private static bool HoldsOtherStampOfPerson(UserEntry user, string securityStamp) =>
        !user.IsBuiltIn && !user.IsLockedByAdministrator && !string.Equals(securityStamp, user.SecurityStamp, StringComparison.Ordinal);

    private async Task ReloadStampAsync(string userId)
    {
        try
        {
            await RefreshUserCoreAsync(userId, signInTime: null, onlyWhenStampChanged: true, CancellationToken.None);
        }
        catch (Exception exception)
        {
            EventLog.StampReloadFailed(userId, exception);
        }
    }

    private async Task RefreshUserCoreAsync(string userId, DateTimeOffset? signInTime, bool onlyWhenStampChanged, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userId);

        var isChanged = false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = _state;
            if (state.ById.TryGetValue(userId, out var user) &&
                await UserDatabase.RefreshUserAsync(_scopeFactory, user, signInTime, cancellationToken) is { } refreshed &&
                (!onlyWhenStampChanged || !string.Equals(refreshed.SecurityStamp, user.SecurityStamp, StringComparison.Ordinal)))
            {
                Publish(state.With(refreshed));
                isChanged = true;
            }
        }
        finally
        {
            _gate.Release();
        }

        // Outside the gate, so that a handler may read or change users.
        if (isChanged)
        {
            RaiseUserChanged(userId);
        }
    }

    internal Task<UserChangeResult> AddUserAsync(string actingUserId, string userName, string? email, IReadOnlyCollection<string> roles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userName);

        var trimmedEmail = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        var normalizedRoles = UserChange.NormalizeRoles(roles);
        return RunAsync(token => AddUserCoreAsync(actingUserId, userName.Trim(), trimmedEmail, normalizedRoles, token), cancellationToken);
    }

    internal Task<UserChangeResult> ChangeUserAsync(string actingUserId, string userName, UserChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userName);

        if (NameRules.GetDisplayNameError(change.DisplayName) is { } displayNameError)
        {
            return Task.FromResult(new UserChangeResult(false, displayNameError));
        }

        return RunAsync(token => ChangeUserCoreAsync(actingUserId, userName, change, token), cancellationToken);
    }

    internal Task<UserChangeResult> SetMcpKeyAsync(string actingUserId, bool create, CancellationToken cancellationToken) =>
        RunAsync(token => SetMcpKeyCoreAsync(actingUserId, create, token), cancellationToken);

    private async Task<UserChangeResult> RunAsync(Func<CancellationToken, Task<(UserChangeResult Result, string? ChangedUserId)>> change, CancellationToken cancellationToken)
    {
        UserChangeResult result;
        string? changedUserId;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            (result, changedUserId) = await change(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        // Outside the gate, so that a handler may read or change users.
        if (changedUserId is not null)
        {
            RaiseUserChanged(changedUserId);
        }

        return result;
    }

    // The caller holds _gate.
    private async Task<(UserChangeResult Result, string? ChangedUserId)> AddUserCoreAsync(
        string actingUserId, string userName, string? email, ReadOnlyCollection<string> roles, CancellationToken cancellationToken)
    {
        if (_rules.GetAddRefusal(_state, actingUserId, userName, roles) is { } refusal)
        {
            return Refused(refusal);
        }

        var committed = await UserDatabase.AddUserAsync(_scopeFactory, userName, email, roles, _timeProvider, cancellationToken);
        if (committed.Error is not null)
        {
            return Refused(committed.Error);
        }

        // Committed: the state takes the user as written, without reading it back.
        var user = committed.User!;
        var actor = GetActorName(_state, actingUserId);
        Publish(_state.With(user));
        EventLog.UserAdded(userName, roles, actor);
        return (new UserChangeResult(true, null, committed.SetupCode), user.Id);
    }

    // The caller holds _gate.
    private async Task<(UserChangeResult Result, string? ChangedUserId)> ChangeUserCoreAsync(
        string actingUserId, string userName, UserChange change, CancellationToken cancellationToken)
    {
        var state = _state;
        var target = state.ByName.GetValueOrDefault(userName);
        if (_rules.GetChangeRefusal(state, actingUserId, userName, target, change) is { } refusal)
        {
            return Refused(refusal);
        }

        return await CommitChangeAsync(state, target!, change, GetActorName(state, actingUserId), cancellationToken);
    }

    // The caller holds _gate.
    private async Task<(UserChangeResult Result, string? ChangedUserId)> SetOwnDisplayNameCoreAsync(
        string userId, UserChange change, CancellationToken cancellationToken)
    {
        var state = _state;
        if (_rules.GetOwnChangeRefusal(state, userId) is { } refusal)
        {
            return Refused(refusal);
        }

        var user = state.ById[userId];
        return await CommitChangeAsync(state, user, change, user.UserName, cancellationToken);
    }

    // The caller holds _gate and checked the change.
    private async Task<(UserChangeResult Result, string? ChangedUserId)> CommitChangeAsync(
        UserState state, UserEntry target, UserChange change, string actor, CancellationToken cancellationToken)
    {
        var committed = await UserDatabase.ChangeUserAsync(_scopeFactory, target, change, _timeProvider, cancellationToken);
        if (committed.Error is not null)
        {
            return Refused(committed.Error);
        }

        // Committed: the state takes the user as written, without reading it back.
        Publish(committed.User is { } user ? state.With(user) : state.Without(target.Id));
        EventLog.UserChanged(target.UserName, change, change.Roles, actor);

        return (new UserChangeResult(true, null, committed.SetupCode), target.Id);
    }

    // The caller holds _gate. The key is returned once and only its hash is kept.
    private async Task<(UserChangeResult Result, string? ChangedUserId)> SetMcpKeyCoreAsync(
        string actingUserId, bool create, CancellationToken cancellationToken)
    {
        var state = _state;
        var mcpUser = state.ById.Values.FirstOrDefault(UserEntry.IsMcpUser);
        if (_rules.GetMcpKeyRefusal(state, actingUserId, mcpUser, create) is { } refusal)
        {
            return Refused(refusal);
        }

        var committed = await UserDatabase.SetMcpKeyAsync(_scopeFactory, mcpUser!.Id, create, cancellationToken);
        if (committed.Error is not null)
        {
            return Refused(committed.Error);
        }

        Publish(state.WithMcpKeyHash(committed.McpKey is { } key ? McpClientKey.Hash(key) : null));
        EventLog.McpKeyChanged(create, GetActorName(state, actingUserId));
        return (new UserChangeResult(true, null, McpKey: committed.McpKey), mcpUser.Id);
    }

    private void RaiseUserChanged(string userId) => EventLog.RaiseUserChanged(UserChanged, userId);

    private static string GetActorName(UserState state, string actingUserId) =>
        state.ById.TryGetValue(actingUserId, out var actor) ? actor.UserName : actingUserId;

    private static (UserChangeResult Result, string? ChangedUserId) Refused(string error) => (new UserChangeResult(false, error), null);

    private IReadOnlyCollection<string> GetRolesByName(string userName) =>
        _state.ByName.TryGetValue(userName, out var user) ? user.Roles : [];

    // Raised under _gate by the role store. The role rows keep their links to the users, so the database already holds
    // the new name.
    private void OnRoleRenamed(string oldName, string newName) => Publish(_state.WithRoleRenamed(oldName, newName));

    // The caller holds _gate. Ordered by name, so the order of the users does not depend on the database.
    private void Publish(UserState state)
    {
        // The state first: it follows the database, which is already committed, even when projecting fails.
        _state = state;
        var now = _timeProvider.GetUtcNow();
        var hasMcpKey = state.McpKeyHash is not null;
        _projection.SetUsers([.. state.ById.Values
            .OrderBy(user => user.UserName, StringComparer.Ordinal)
            .Select(user => user.ToSnapshot(now, hasMcpKey && UserEntry.IsMcpUser(user)))]);
    }
}
