namespace HomeBlaze.Authentication.Blazor;

/// <summary>Marks the endpoints of the pages of the app and its live connection, see <see cref="AuthenticationWebExtensions.WithHomeBlazeAccountGate"/>.</summary>
internal sealed class AppEndpointMetadata
{
    public static AppEndpointMetadata Instance { get; } = new();

    private AppEndpointMetadata()
    {
    }
}
