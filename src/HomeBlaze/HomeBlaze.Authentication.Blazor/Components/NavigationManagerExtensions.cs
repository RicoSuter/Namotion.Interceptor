using Microsoft.AspNetCore.Components;

namespace HomeBlaze.Authentication.Blazor.Components;

internal static class NavigationManagerExtensions
{
    /// <summary>The path and the query of the address the browser shows, which is where a person returns to after signing in.</summary>
    public static string GetPathAndQuery(this NavigationManager navigation) => new Uri(navigation.Uri).PathAndQuery;
}
