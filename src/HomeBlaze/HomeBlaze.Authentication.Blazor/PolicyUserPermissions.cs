using HomeBlaze.Authorization.Abstractions;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry.Abstractions;
using AuthorizationService = HomeBlaze.Authorization.AuthorizationService;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>
/// The permissions of the person of the current circuit: the compiled policy of the roles and kind of
/// <see cref="CurrentUser"/>. <c>administer</c> needs a full administrator, except on user management (the members of a
/// <see cref="Authorization.User"/>, and <see cref="Authorization.Authorization.Users"/> and adding users), which a scoped
/// grant allows. A signed-in person is also shown the users they may administer and reads their members and history,
/// which the system rules otherwise hide. The members of an <see cref="Authorization.IRoleScopedSubject"/> it scopes are
/// denied to a person who may not act through its roles, see <see cref="CanActThrough"/>. The order in the metadata of a
/// folder names only the children the person is shown. A person who must change their password is denied everything
/// until they change it. Scoped per circuit; thread-safe.
/// </summary>
internal sealed partial class PolicyUserPermissions : IUserPermissions, IDisposable
{
    private readonly AuthorizationService _authorizationService;
    private readonly CurrentUser _currentUser;
    private readonly ILogger<PolicyUserPermissions> _logger;
    private readonly Lock _computeLock = new();

    // Replaced as a whole under _computeLock, so that a computation from older inputs never overwrites a newer one.
    private volatile PermissionDecisions _decisions = PermissionDecisions.DenyAll;

    public PolicyUserPermissions(AuthorizationService authorizationService, CurrentUser currentUser, ILogger<PolicyUserPermissions> logger)
    {
        ArgumentNullException.ThrowIfNull(authorizationService);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(logger);

        _authorizationService = authorizationService;
        _currentUser = currentUser;
        _logger = logger;

        // Subscribed first, so that a change during the first computation is not missed.
        _authorizationService.PoliciesChanged += OnChanged;
        _currentUser.Changed += OnChanged;
        Recompute();
    }

    /// <summary>Raised when the answers may have changed; a handler that throws is logged and skips no other handler.</summary>
    public event Action? Changed;

    /// <inheritdoc />
    public bool CanRead(RegisteredSubjectProperty member) => _decisions.CanRead(member);

    /// <inheritdoc />
    public object? GetValue(RegisteredSubjectProperty member) => _decisions.GetValue(member);

    /// <inheritdoc />
    public bool CanWrite(RegisteredSubjectProperty member) => _decisions.CanWrite(member);

    /// <inheritdoc />
    public bool CanInvoke(RegisteredSubjectProperty member) => _decisions.CanInvoke(member);

    /// <inheritdoc />
    public bool CanAdminister(RegisteredSubjectProperty member) => _decisions.CanAdminister(member);

    /// <inheritdoc />
    public bool CanReadHistory(RegisteredSubjectProperty member) => _decisions.CanReadHistory(member);

    /// <inheritdoc />
    public bool CanShow(RegisteredSubject subject) => _decisions.CanShow(subject);

    /// <inheritdoc />
    public bool HoldsAllRoles(IReadOnlyCollection<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        return _decisions.HoldsAllRoles(roles);
    }

    /// <inheritdoc />
    public bool CanActThrough(IReadOnlyCollection<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        return _decisions.CanActThrough(roles);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetRolesWithDenyRulesMissingFrom(IReadOnlyCollection<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        return _decisions.GetRolesWithDenyRulesMissingFrom(roles);
    }

    public void Dispose()
    {
        _authorizationService.PoliciesChanged -= OnChanged;
        _currentUser.Changed -= OnChanged;
    }

    // Raised by the authorization service and the user store for every circuit, so it never throws.
    private void OnChanged()
    {
        Recompute();
        ChangedHandlers.Raise(Changed, _logger, nameof(PolicyUserPermissions));
    }

    private void Recompute()
    {
        lock (_computeLock)
        {
            try
            {
                _decisions = PermissionDecisions.Create(_authorizationService, _currentUser.GetPolicyInputs());
            }
            catch (Exception exception)
            {
                LogComputeFailed(_logger, exception);
                _decisions = PermissionDecisions.DenyAll;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The policy of the user could not be computed; the user may see and do nothing.")]
    private static partial void LogComputeFailed(ILogger logger, Exception exception);
}
