using System.Security.Claims;
using HomeBlaze.Authorization.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AuthorizationService = HomeBlaze.Authorization.AuthorizationService;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>
/// The permissions of the person or visitor of a request outside a circuit: decided once from the user of the request and
/// the roles the <see cref="UserStore"/> holds for them, by the rules of <see cref="PolicyUserPermissions"/>. Thread-safe.
/// </summary>
internal sealed partial class RequestUserPermissionsFactory : IRequestUserPermissionsFactory
{
    private readonly AuthorizationService _authorizationService;
    private readonly UserStore _userStore;
    private readonly string _userIdClaimType;
    private readonly ILogger<RequestUserPermissionsFactory> _logger;

    public RequestUserPermissionsFactory(
        AuthorizationService authorizationService,
        UserStore userStore,
        IOptions<IdentityOptions> identityOptions,
        ILogger<RequestUserPermissionsFactory> logger)
    {
        ArgumentNullException.ThrowIfNull(authorizationService);
        ArgumentNullException.ThrowIfNull(userStore);
        ArgumentNullException.ThrowIfNull(identityOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _authorizationService = authorizationService;
        _userStore = userStore;
        _userIdClaimType = identityOptions.Value.ClaimsIdentity.UserIdClaimType;
        _logger = logger;
    }

    /// <inheritdoc />
    public IUserPermissions Create(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        try
        {
            var inputs = UserIdentity.From(user, _userIdClaimType).GetPolicyInputs(_userStore);
            return PermissionDecisions.Create(_authorizationService, inputs);
        }
        catch (Exception exception)
        {
            LogComputeFailed(_logger, exception);
            return PermissionDecisions.DenyAll;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The policy of the user of a request could not be computed; the request may see nothing.")]
    private static partial void LogComputeFailed(ILogger logger, Exception exception);
}
