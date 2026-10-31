namespace HomeBlaze.Services.Clients;

/// <summary>
/// The connection of the circuit, registered as a scoped service and set once the circuit is interactive.
/// </summary>
public sealed class CurrentClientConnection
{
    /// <summary>
    /// The connection of this circuit, or null before the circuit is interactive and after it closed.
    /// </summary>
    public ClientConnection? Connection { get; set; }
}
