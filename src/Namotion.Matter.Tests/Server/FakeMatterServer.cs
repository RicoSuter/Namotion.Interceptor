using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Namotion.Matter.Tests.Server;

/// <summary>Raised by a handler to answer a request with an error response.</summary>
public sealed class FakeMatterServerErrorException(JsonObject response) : Exception
{
    public JsonObject Response { get; } = response;
}

public sealed class FakeMatterServer : IAsyncDisposable
{
    private readonly TaskCompletionSource<WebSocket> _client = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closeClient = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Not disposed: a connection handler can still send while the server stops.
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private WebApplication _application = null!;

    private FakeMatterServer()
    {
    }

    public Uri Url { get; private set; } = null!;

    /// <summary>The first message sent to a connecting client, or null to send none.</summary>
    public JsonObject? ServerInfo { get; set; } = CreateServerInfo(schemaVersion: 15, minSupportedSchemaVersion: 11);

    /// <summary>Answers a request with its <c>result</c>, or throws <see cref="FakeMatterServerErrorException"/>; null sends no answer.</summary>
    public Func<JsonObject, JsonNode?>? Handler { get; set; } = _ => null;

    /// <summary>Whether a request without a handler result is left unanswered rather than answered with a null result.</summary>
    public bool LeaveUnansweredRequestsOpen { get; set; }

    public Channel<JsonObject> Requests { get; } = Channel.CreateUnbounded<JsonObject>();

    public static JsonObject CreateServerInfo(int schemaVersion, int minSupportedSchemaVersion) => new()
    {
        ["fabric_id"] = 1,
        ["compressed_fabric_id"] = 18446744073709551615UL,
        ["schema_version"] = schemaVersion,
        ["min_supported_schema_version"] = minSupportedSchemaVersion,
        ["sdk_version"] = "fake/1.0",
        ["wifi_credentials_set"] = false,
        ["thread_credentials_set"] = true,
        ["bluetooth_enabled"] = true
    };

    public static async Task<FakeMatterServer> StartAsync(string? unixSocketPath = null)
    {
        var server = new FakeMatterServer();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            if (unixSocketPath is null)
            {
                options.Listen(IPAddress.Loopback, 0);
            }
            else
            {
                options.ListenUnixSocket(unixSocketPath);
            }
        });

        var application = builder.Build();
        application.UseWebSockets();
        application.Map("/ws", context => server.HandleConnectionAsync(context));
        await application.StartAsync();

        server._application = application;
        server.Url = unixSocketPath is null
            ? new Uri(application.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses
                .Single().Replace("http://", "ws://", StringComparison.Ordinal) + "/ws")
            : new Uri("unix://" + unixSocketPath);
        return server;
    }

    public async Task SendEventAsync(string eventName, JsonNode? data)
    {
        var client = await _client.Task;
        await SendAsync(client, new JsonObject { ["event"] = eventName, ["data"] = data });
    }

    /// <summary>Answers a request that was left open with its <c>result</c>.</summary>
    public async Task AnswerAsync(JsonObject request, JsonNode? result)
    {
        var client = await _client.Task;
        await SendAsync(client, CreateResponse(request, result));
    }

    /// <summary>Closes the connection to the client from the server side.</summary>
    public void CloseClient() => _closeClient.TrySetResult();

    public async ValueTask DisposeAsync()
    {
        _closeClient.TrySetResult();
        await _application.DisposeAsync();
    }

    private static JsonObject CreateResponse(JsonObject request, JsonNode? result) =>
        new() { ["message_id"] = request["message_id"]!.GetValue<string>(), ["result"] = result };

    private async Task HandleConnectionAsync(HttpContext context)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        if (ServerInfo is { } serverInfo)
        {
            await SendAsync(socket, serverInfo.DeepClone());
        }

        _client.TrySetResult(socket);

        var receiveTask = ReceiveRequestsAsync(socket, context.RequestAborted);
        await Task.WhenAny(receiveTask, _closeClient.Task);
        if (_closeClient.Task.IsCompleted)
        {
            await CloseOutputAsync(socket);
        }

        // The socket is disposed only after the receive ended, when the client disconnects or the server stops.
        await receiveTask;
    }

    private async Task ReceiveRequestsAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 1024];
        try
        {
            while (await ReceiveMessageAsync(socket, buffer, cancellationToken) is var length and >= 0)
            {
                var request = JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, length))!.AsObject();
                await Requests.Writer.WriteAsync(request, cancellationToken);
                await AnswerRequestAsync(socket, request);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
        {
            // The client disconnected or the server stopped.
        }
    }

    /// <summary>Receives one message into the buffer; returns its length, or -1 when the client closed.</summary>
    private static async Task<int> ReceiveMessageAsync(WebSocket socket, byte[] buffer, CancellationToken cancellationToken)
    {
        var length = 0;
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(length), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return -1;
            }

            length += result.Count;
        }
        while (!result.EndOfMessage);

        return length;
    }

    private async Task AnswerRequestAsync(WebSocket socket, JsonObject request)
    {
        JsonObject response;
        try
        {
            var result = Handler?.Invoke(request);
            if (result is null && LeaveUnansweredRequestsOpen)
            {
                return;
            }

            response = CreateResponse(request, result);
        }
        catch (FakeMatterServerErrorException error)
        {
            response = error.Response;
            response["message_id"] = request["message_id"]!.GetValue<string>();
        }

        await SendAsync(socket, response);
    }

    private async Task SendAsync(WebSocket socket, JsonNode message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString(JsonSerializerOptions.Default));
        await _sendLock.WaitAsync();
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task CloseOutputAsync(WebSocket socket)
    {
        await _sendLock.WaitAsync();
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed by test", CancellationToken.None);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
