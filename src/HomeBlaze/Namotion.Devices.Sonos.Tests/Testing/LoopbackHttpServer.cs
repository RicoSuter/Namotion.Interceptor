using System.Net;
using Namotion.Interceptor.Testing;

namespace Namotion.Devices.Sonos.Tests.Testing;

/// <summary>
/// A minimal HTTP server on 127.0.0.1 that hands each request to a handler and closes the response.
/// </summary>
internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly Func<HttpListenerContext, Task> _handler;
    private readonly Task _loop;
    private int _disposed;

    internal LoopbackHttpServer(Func<HttpListenerContext, Task> handler)
    {
        _handler = handler;
        HttpListener? listener = null;
        Port = LoopbackPorts.StartOnFreePort(port =>
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
