namespace HomeBlaze.Authentication.Blazor.Components;

/// <summary>
/// What the server saw of the transport of the request that rendered the app, carried into the circuit by the
/// <see cref="AccountGate"/>. Scoped per circuit.
/// </summary>
internal sealed class RequestTransport
{
    /// <summary>
    /// Whether the request was HTTPS after the forwarded headers were applied, so whether the session cookie is secure.
    /// False while unknown, so that the warning of the dialogs shows.
    /// </summary>
    public bool ServerSawHttps { get; set; }
}
