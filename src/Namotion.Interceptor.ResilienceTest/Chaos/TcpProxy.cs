using System.Net;
using System.Net.Sockets;

namespace Namotion.Interceptor.ResilienceTest.Chaos;

/// <summary>
/// A TCP relay that sits between a client and server, enabling chaos injection.
/// Supports pausing (drops bytes to simulate partition) and closing connections.
/// </summary>
public sealed class TcpProxy : IAsyncDisposable
{
    private readonly int _listenPort;
    private readonly int _targetPort;
    private readonly ILogger _logger;
    private readonly List<(TcpClient Client, TcpClient Server)> _activeConnections = [];
    private readonly Lock _connectionsLock = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _acceptCancellation;
    private Task? _acceptTask;
    private volatile bool _isPaused;

    public TcpProxy(int listenPort, int targetPort, ILogger logger)
    {
        _listenPort = listenPort;
        _targetPort = targetPort;
        _logger = logger;
    }

    public int ListenPort => _listenPort;
    public int TargetPort => _targetPort;
    public bool IsPaused => _isPaused;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _acceptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Loopback, _listenPort);
        _listener.Start();
        _acceptTask = AcceptLoopAsync(_acceptCancellation.Token);
        _logger.LogInformation("TcpProxy started: localhost:{ListenPort} -> localhost:{TargetPort}", _listenPort, _targetPort);
    }

    /// <summary>
    /// Simulates network partition by dropping all forwarded bytes.
    /// Existing connections stay open but no data flows.
    /// </summary>
    public void PauseForwarding()
    {
        _isPaused = true;
        _logger.LogWarning("TcpProxy:{ListenPort} PAUSED (partition)", _listenPort);
    }

    /// <summary>
    /// Resumes byte forwarding after a pause.
    /// </summary>
    public void ResumeForwarding()
    {
        _isPaused = false;
        _logger.LogInformation("TcpProxy:{ListenPort} RESUMED", _listenPort);
    }

    /// <summary>
    /// Closes all active connections. Simulates process crash / clean disconnect.
    /// New connections are still accepted.
    /// </summary>
    public void CloseAllConnections()
    {
        lock (_connectionsLock)
        {
            _logger.LogWarning("TcpProxy:{ListenPort} closing {Count} connections", _listenPort, _activeConnections.Count);
            foreach (var (client, server) in _activeConnections)
            {
                client.Dispose();
                server.Dispose();
            }
            _activeConnections.Clear();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? clientSocket = null;
            try
            {
                clientSocket = await _listener!.AcceptTcpClientAsync(cancellationToken);
                var serverSocket = new TcpClient();
                await serverSocket.ConnectAsync(IPAddress.Loopback, _targetPort, cancellationToken);

                lock (_connectionsLock)
                {
                    _activeConnections.Add((clientSocket, serverSocket));
                }

                _ = ForwardAsync(clientSocket, serverSocket, cancellationToken);
                _ = ForwardAsync(serverSocket, clientSocket, cancellationToken);

                // Handoff successful - don't dispose in catch
                clientSocket = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                clientSocket?.Dispose();
                break;
            }
            catch (Exception exception)
            {
                clientSocket?.Dispose();
                _logger.LogDebug(exception, "TcpProxy:{ListenPort} accept error", _listenPort);
            }
        }
    }

    private async Task ForwardAsync(TcpClient source, TcpClient target, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            var sourceStream = source.GetStream();
            var targetStream = target.GetStream();

            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await sourceStream.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0)
                    break;

                if (_isPaused)
                    continue; // drop bytes - simulates partition

                await targetStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            }
        }
        catch (Exception)
        {
            // Connection closed - expected during chaos
        }
        finally
        {
            RemoveConnection(source, target);
        }
    }

    private void RemoveConnection(TcpClient source, TcpClient target)
    {
        lock (_connectionsLock)
        {
            _activeConnections.RemoveAll(connection =>
                connection.Client == source || connection.Client == target ||
                connection.Server == source || connection.Server == target);
        }
        source.Dispose();
        target.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _acceptCancellation?.Cancel();
        _listener?.Stop();
        if (_acceptTask != null)
        {
            try { await _acceptTask; } catch (OperationCanceledException) { }
        }
        CloseAllConnections();
        _acceptCancellation?.Dispose();
    }
}
