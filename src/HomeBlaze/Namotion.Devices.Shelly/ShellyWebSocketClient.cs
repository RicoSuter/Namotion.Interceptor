using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Namotion.Devices.Shelly;

/// <summary>
/// WebSocket client for real-time push updates from Shelly Gen2 devices.
/// Runs as a managed task alongside the polling loop.
/// </summary>
internal sealed class ShellyWebSocketClient : IDisposable
{
    private readonly ShellyDevice _device;
    private readonly ILogger _logger;
    private readonly TimeSpan _reconnectDelay = TimeSpan.FromSeconds(30);

    private ClientWebSocket? _webSocket;

    public ShellyWebSocketClient(ShellyDevice device, ILogger logger)
    {
        _device = device;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[32 * 1024];

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _webSocket?.Dispose();
                _webSocket = new ClientWebSocket();

                var uri = new Uri($"ws://{_device.HostAddress}/rpc");
                await _webSocket.ConnectAsync(uri, cancellationToken);

                _logger.LogDebug("WebSocket connected to {HostAddress}", _device.HostAddress);

                // Request full status on connect
                var request = JsonSerializer.Serialize(new
                {
                    id = 1,
                    src = $"HomeBlaze:{_device.HostAddress}",
                    method = "Shelly.GetStatus",
                    @params = new { }
                });
                var requestBytes = Encoding.UTF8.GetBytes(request);
                await _webSocket.SendAsync(requestBytes, WebSocketMessageType.Text, true, cancellationToken);

                // Receive loop
                await ReceiveLoopAsync(buffer, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "WebSocket disconnected from {HostAddress}", _device.HostAddress);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_reconnectDelay, cancellationToken);
            }
        }
    }

    private async Task ReceiveLoopAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        using var messageBuffer = new MemoryStream();

        while (_webSocket?.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            messageBuffer.SetLength(0);

            WebSocketReceiveResult result;
            do
            {
                result = await _webSocket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;

                messageBuffer.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (messageBuffer.Length > 0)
            {
                HandleMessage(messageBuffer.GetBuffer().AsMemory(0, (int)messageBuffer.Length));
            }
        }
    }

    private void HandleMessage(ReadOnlyMemory<byte> utf8Json)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8Json);
            var root = document.RootElement;

            if (root.TryGetProperty("method", out var method) &&
                method.GetString() == "NotifyStatus")
            {
                if (root.TryGetProperty("params", out var paramsElement))
                    _device.ParseStatusComponents(paramsElement, isPartialUpdate: true);
            }
            else if (root.TryGetProperty("result", out var resultElement))
            {
                _device.ParseStatusComponents(resultElement);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to parse WebSocket message from {HostAddress}", _device.HostAddress);
        }
    }

    public void Dispose()
    {
        try
        {
            _webSocket?.Dispose();
        }
        catch
        {
            // Ignore errors during disposal
        }

        _webSocket = null;
    }
}
