using System.Net;
using System.Net.Sockets;

namespace Namotion.Devices.Sonos.Tests.Testing;

/// <summary>
/// A minimal HTTP server on 127.0.0.1 that hands each request to a handler and closes the response.
/// </summary>
internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<HttpListenerContext, Task> _handler;
    private readonly Task _loop;

    internal LoopbackHttpServer(Func<HttpListenerContext, Task> handler)
    {
        _handler = handler;
        Port = GetFreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
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
            context.Response.StatusCode = 500;
            Console.Error.WriteLine(exception);
        }
        finally
        {
            context.Response.Close();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        _listener.Close();
        await _loop;
    }
}
