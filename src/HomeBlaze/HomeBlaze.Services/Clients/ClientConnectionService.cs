using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Services.Clients;

/// <summary>
/// Tracks the open circuits of the application, independently of whether an installation subject exists.
/// </summary>
public sealed class ClientConnectionService
{
    private const string IdCharacters = "abcdefghijklmnopqrstuvwxyz234567";
    private const int IdLength = 12;

    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private ImmutableArray<ClientConnection> _connections = [];
    private Func<ClientConnection, string, CancellationToken, Task>? _renameHandler;

    public ClientConnectionService(TimeProvider timeProvider, ILogger<ClientConnectionService> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// The open circuits, as a snapshot.
    /// </summary>
    public ImmutableArray<ClientConnection> Connections
    {
        get
        {
            lock (_lock)
            {
                return _connections;
            }
        }
    }

    /// <summary>
    /// Raised after a circuit connected or disconnected, never while a lock is held. A subscriber that throws is
    /// logged and neither stops the other subscribers nor fails the connect or disconnect.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Creates a random client id of 12 lowercase base32 characters.
    /// </summary>
    public static string CreateClientId() => RandomNumberGenerator.GetString(IdCharacters, IdLength);

    /// <summary>
    /// Adds an open circuit with a new random connection id and raises <see cref="Changed"/>.
    /// </summary>
    public ClientConnection Connect(string? cookieClientId, IClientConnectionCallbacks callbacks)
    {
        var connection = new ClientConnection(CreateClientId(), cookieClientId, _timeProvider.GetUtcNow(), callbacks);
        lock (_lock)
        {
            _connections = _connections.Add(connection);
        }

        RaiseChanged();
        return connection;
    }

    /// <summary>
    /// Removes the circuit and raises <see cref="Changed"/>; does nothing for a circuit that is not open.
    /// </summary>
    public void Disconnect(ClientConnection connection)
    {
        lock (_lock)
        {
            var connections = _connections.Remove(connection);
            if (connections.Length == _connections.Length)
            {
                return;
            }

            _connections = connections;
        }

        RaiseChanged();
    }

    /// <summary>
    /// Sets what renames the client of a connection, until the returned registration is disposed.
    /// </summary>
    public IDisposable SetRenameHandler(Func<ClientConnection, string, CancellationToken, Task> handler)
    {
        lock (_lock)
        {
            _renameHandler = handler;
        }

        return new RenameHandlerRegistration(this, handler);
    }

    /// <summary>
    /// Names the client of the connection.
    /// </summary>
    /// <exception cref="InvalidOperationException">No installation runs that could name it.</exception>
    public Task RenameAsync(ClientConnection connection, string name, CancellationToken cancellationToken)
    {
        Func<ClientConnection, string, CancellationToken, Task>? handler;
        lock (_lock)
        {
            handler = _renameHandler;
        }

        return handler is null
            ? throw new InvalidOperationException("No installation runs that could name this browser.")
            : handler(connection, name, cancellationToken);
    }

    private void RaiseChanged()
    {
        // Connect runs in the initialization of a circuit, so an escaping exception would fail the circuit after
        // its connection was added, and Disconnect would never remove it.
        foreach (var handler in Delegate.EnumerateInvocationList(Changed))
        {
            try
            {
                handler();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Handling a change of the client connections failed");
            }
        }
    }

    private void ClearRenameHandler(Func<ClientConnection, string, CancellationToken, Task> handler)
    {
        lock (_lock)
        {
            if (_renameHandler == handler)
            {
                _renameHandler = null;
            }
        }
    }

    private sealed class RenameHandlerRegistration(ClientConnectionService service, Func<ClientConnection, string, CancellationToken, Task> handler) : IDisposable
    {
        public void Dispose() => service.ClearRenameHandler(handler);
    }
}
