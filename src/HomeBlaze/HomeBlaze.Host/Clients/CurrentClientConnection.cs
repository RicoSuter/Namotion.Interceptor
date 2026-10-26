using HomeBlaze.Services.Clients;

namespace HomeBlaze.Host.Clients;

/// <summary>
/// The connection of the circuit, set once the circuit is interactive.
/// </summary>
internal sealed class CurrentClientConnection
{
    public ClientConnection? Connection { get; set; }
}
