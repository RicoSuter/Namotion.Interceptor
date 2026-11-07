using HomeBlaze.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>
/// Requires a signed-in person whose roles, as the <see cref="UserStore"/> holds them now, make them a full administrator,
/// see <see cref="AuthorizationServiceRoleExtensions.IsFullAdministrator(AuthorizationService, IReadOnlyCollection{string})"/>.
/// </summary>
public sealed class FullAdministratorRequirement : IAuthorizationRequirement;
