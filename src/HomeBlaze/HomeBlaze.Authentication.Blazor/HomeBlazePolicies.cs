namespace HomeBlaze.Authentication.Blazor;

/// <summary>The names of the authorization policies that <see cref="AuthenticationWebExtensions.AddHomeBlazeWebAuthentication"/> adds.</summary>
public static class HomeBlazePolicies
{
    /// <summary>Requires a signed-in person who is a full administrator, see <see cref="FullAdministratorRequirement"/>.</summary>
    public const string FullAdministrator = "HomeBlaze.FullAdministrator";
}
