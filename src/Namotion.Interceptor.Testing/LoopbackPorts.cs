using System.Net;
using System.Net.Sockets;

namespace Namotion.Interceptor.Testing;

/// <summary>
/// Free TCP ports on the loopback interface for test servers.
/// </summary>
public static class LoopbackPorts
{
    private const int MaxStartAttempts = 5;

    /// <summary>
    /// Returns a port that is free on 127.0.0.1 right now. Another process can take it before it is bound; prefer
    /// <see cref="StartOnFreePort"/> when the caller binds it.
    /// </summary>
    public static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Starts something on a free port and returns the port. A start that fails with
    /// <see cref="HttpListenerException"/> or <see cref="SocketException"/>, because the port was taken in between, is
    /// retried on another port, up to five attempts.
    /// </summary>
    /// <param name="start">Binds whatever the test needs to the given port.</param>
    public static int StartOnFreePort(Action<int> start)
    {
        for (var attempt = 1; ; attempt++)
        {
            var port = GetFreePort();
            try
            {
                start(port);
                return port;
            }
            catch (Exception exception) when (exception is HttpListenerException or SocketException && attempt < MaxStartAttempts)
            {
                // Taken in between; try another port.
            }
        }
    }
}
