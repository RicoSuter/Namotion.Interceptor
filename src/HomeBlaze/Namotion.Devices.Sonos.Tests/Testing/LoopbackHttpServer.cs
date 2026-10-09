using System.Net;
using System.Net.Sockets;

namespace Namotion.Devices.Sonos.Tests.Testing;

/// <summary>
/// A minimal HTTP server on 127.0.0.1 that hands each request to a handler and closes the response.
/// </summary>
internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    private const int MaxStartAttempts = 5;

    private readonly HttpListener _listener;
    private readonly Func<HttpListenerContext, Task> _handler;
    private readonly Task _loop;
    private int _disposed;

    internal LoopbackHttpServer(Func<HttpListenerContext, Task> handler)
    {
        _handler = handler;
        HttpListener? listener = null;
        Port = StartOnFreePort(port =>
        {
            var candidate = new HttpListener();
            candidate.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                candidate.Start();
            }
            catch
            {
                candidate.Close();
                throw;
            }

            listener = candidate;
        });
        _listener = listener!;
        _loop = RunAsync();
    }

    internal int Port { get; }

    internal Uri BaseUri => new($"http://127.0.0.1:{Port}/");

    internal static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Starts something on a free port and returns the port. Another process can take a free port before it is bound,
    /// so a start that fails with <see cref="HttpListenerException"/> is retried on another port.
    /// </summary>
    internal static int StartOnFreePort(Action<int> start)
    {
        for (var attempt = 1; ; attempt++)
        {
            var port = GetFreePort();
            try
            {
                start(port);
                return port;
            }
            catch (HttpListenerException) when (attempt < MaxStartAttempts)
            {
                // Taken in between; try another port.
            }
        }
    }

    private async Task RunAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            await _handler(context);
        }
        catch (Exception exception)
        {
            TrySetStatus(context.Response, 500);
            Console.Error.WriteLine(exception);
        }

        try
        {
            context.Response.Close();
        }
        catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
        {
            try
            {
                context.Response.Abort();
            }
            catch (Exception abortException) when (abortException is HttpListenerException or ObjectDisposedException)
            {
                // The connection is already gone.
            }
        }
    }

    private static void TrySetStatus(HttpListenerResponse response, int statusCode)
    {
        try
        {
            response.StatusCode = statusCode;
        }
        catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
        {
            // The response is already sent or the connection is gone.
        }
    }

    /// <summary>
    /// Stops the server; a test may call it early to take a speaker offline, so a second call does nothing.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _listener.Stop();
        _listener.Close();
        await _loop;
    }
}
