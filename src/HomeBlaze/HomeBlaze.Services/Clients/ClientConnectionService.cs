using System.Buffers.Text;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Services.Clients;

/// <summary>
/// Tracks the open circuits of the application, independently of whether an installation subject exists.
/// </summary>
public sealed class ClientConnectionService
{
    private const string IdCharacters = "abcdefghijklmnopqrstuvwxyz234567";
    private const int IdLength = 12;
    private const int TokenByteCount = 32;
    private const int MaximumStackTokenByteCount = 256;

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
    /// Creates a secret client token of 32 random bytes, encoded as 43 base64url characters without padding.
    /// </summary>
    public static string CreateClientToken()
    {
        Span<byte> bytes = stackalloc byte[TokenByteCount];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url.EncodeToString(bytes);
    }

    /// <summary>
    /// Returns the SHA-256 hash of the UTF-8 encoded token as 64 lowercase hexadecimal characters.
    /// </summary>
    public static string HashClientToken(string token)
    {
        ArgumentNullException.ThrowIfNull(token);

        // The token comes from a cookie, so its length is not trusted for the stack.
        var byteCount = Encoding.UTF8.GetByteCount(token);
        var bytes = byteCount <= MaximumStackTokenByteCount ? stackalloc byte[MaximumStackTokenByteCount] : new byte[byteCount];
        var written = Encoding.UTF8.GetBytes(token, bytes);

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes[..written], hash);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Adds an open circuit with a new random connection id and the hash of the client token its browser stores, if
    /// any, and raises <see cref="Changed"/>.
    /// </summary>
    public ClientConnection Connect(string? cookieToken, IClientConnectionCallbacks callbacks)
    {
        var cookieTokenHash = string.IsNullOrEmpty(cookieToken) ? null : HashClientToken(cookieToken);
        var connection = new ClientConnection(CreateClientId(), cookieTokenHash, _timeProvider.GetUtcNow(), callbacks);
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
