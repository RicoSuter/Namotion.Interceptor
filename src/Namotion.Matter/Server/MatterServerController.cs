using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Namotion.Matter.Server;

/// <summary>
/// A connection to a matterjs-server (the Open Home Foundation Matter Server) over its WebSocket API. A closed
/// connection is not reopened; create a new controller instead.
/// </summary>
public sealed class MatterServerController : IMatterController
{
    private const ulong FirstGroupNodeId = 0xFFFF_FFFF_FFFF_0000;
    private static readonly TimeSpan MaxTimedRequestTimeout = TimeSpan.FromMilliseconds(ushort.MaxValue);
    private static readonly TimeSpan MinCommissioningWindowTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan MaxCommissioningWindowTimeout = TimeSpan.FromSeconds(900);

    private readonly MatterServerConnection _connection;
    private readonly Channel<MatterControllerEvent> _events = Channel.CreateUnbounded<MatterControllerEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private volatile MatterServerInfo _serverInfo;

    private MatterServerController(MatterServerConnection connection)
    {
        _connection = connection;
        _serverInfo = connection.ServerInfo;
    }

    /// <inheritdoc />
    public MatterServerInfo ServerInfo => _serverInfo;

    /// <inheritdoc />
    public ChannelReader<MatterControllerEvent> Events => _events.Reader;

    /// <summary>Connects to a server and checks that its schema version is supported.</summary>
    /// <param name="serverUrl">The server URL: <c>ws://host:5580/ws</c>, <c>wss://...</c> or <c>unix:///path/to/socket</c>.</param>
    /// <param name="options">The options, or null for the defaults.</param>
    /// <param name="logger">The logger, or null for none.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <exception cref="ArgumentException">The URL scheme is not supported.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is not positive, or a timeout exceeds about 49 days.</exception>
    /// <exception cref="WebSocketException">The server refused the connection, or closed it before sending its information.</exception>
    /// <exception cref="MatterException">The server's information could not be read.</exception>
    /// <exception cref="NotSupportedException">The server's schema version is not supported.</exception>
    /// <exception cref="TimeoutException">The server did not accept the connection and send its information within <see cref="MatterServerOptions.ConnectTimeout"/>.</exception>
    public static async Task<MatterServerController> ConnectAsync(
        Uri serverUrl,
        MatterServerOptions? options = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverUrl);
        options ??= new MatterServerOptions();
        options.ThrowIfInvalid();
        var connection = await MatterServerConnection.ConnectAsync(serverUrl, options, logger, cancellationToken).ConfigureAwait(false);
        try
        {
            MatterServerResults.ThrowIfUnsupported(connection.ServerInfo);
            var controller = new MatterServerController(connection);
            connection.StartReceiving(controller.HandleEvent, controller.HandleConnectionLost);
            return controller;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MatterNode>> StartListeningAsync(CancellationToken cancellationToken = default)
    {
        var result = await _connection.SendRequestAsync("start_listening", null, cancellationToken).ConfigureAwait(false);
        return MatterServerResults.ReadNodes(result);
    }

    /// <inheritdoc />
    public async Task WriteAttributeAsync(ulong nodeId, AttributePath path, IMatterPayload value, CancellationToken cancellationToken = default)
    {
        ThrowIfGroupNodeId(nodeId);
        ArgumentNullException.ThrowIfNull(value);
        var result = await _connection.SendRequestAsync("write_attribute", writer =>
        {
            writer.WriteNumber("node_id", nodeId);
            writer.WriteString("attribute_path", path.ToString());
            JsonMatterValueWriter.WriteProperty(writer, "value", value);
        }, cancellationToken).ConfigureAwait(false);

        MatterServerResults.ThrowIfWriteFailed(result, nodeId, path);
    }

    /// <inheritdoc />
    public async Task<MatterValue> InvokeAsync(
        ulong nodeId,
        CommandPath path,
        string commandName,
        IMatterPayload? payload = null,
        TimeSpan? timedRequestTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfGroupNodeId(nodeId);
        ArgumentException.ThrowIfNullOrEmpty(commandName);
        if (timedRequestTimeout is { } timedTimeout && (timedTimeout < TimeSpan.Zero || timedTimeout > MaxTimedRequestTimeout))
        {
            throw new ArgumentOutOfRangeException(nameof(timedRequestTimeout), timedTimeout, "A timed request timeout is from 0 to 65535 milliseconds.");
        }

        var result = await _connection.SendRequestAsync("device_command", writer =>
        {
            writer.WriteNumber("node_id", nodeId);
            writer.WriteNumber("endpoint_id", path.Endpoint);
            writer.WriteNumber("cluster_id", path.Cluster);
            writer.WriteString("command_name", commandName);
            JsonMatterValueWriter.WriteProperty(writer, "payload", payload);
            if (timedRequestTimeout is { } timeout)
            {
                writer.WriteNumber("timed_request_timeout_ms", (int)timeout.TotalMilliseconds);
            }
        }, cancellationToken).ConfigureAwait(false);

        return new MatterValue(result);
    }

    /// <inheritdoc />
    public async Task<MatterNode> CommissionAsync(string code, bool networkOnly, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        var result = await _connection.SendRequestAsync("commission_with_code", writer =>
        {
            writer.WriteString("code", code);
            writer.WriteBoolean("network_only", networkOnly);
        }, cancellationToken, withoutTimeout: true).ConfigureAwait(false);

        return MatterServerResults.ReadCommissionedNode(result);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MatterOnboardingPayload>> ParseOnboardingPayloadAsync(string code, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        var result = await _connection.SendRequestAsync("parse_onboarding_payload", writer => writer.WriteString("code", code), cancellationToken)
            .ConfigureAwait(false);
        return MatterServerResults.ReadOnboardingPayloads(result);
    }

    /// <inheritdoc />
    public Task SetWifiCredentialsAsync(string ssid, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(ssid);
        ArgumentException.ThrowIfNullOrEmpty(password);
        return _connection.SendRequestAsync("set_wifi_credentials", writer =>
        {
            writer.WriteString("ssid", ssid);
            writer.WriteString("credentials", password);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task SetThreadDatasetAsync(string dataset, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataset);
        return _connection.SendRequestAsync("set_thread_dataset", writer => writer.WriteString("dataset", dataset), cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveNodeAsync(ulong nodeId, CancellationToken cancellationToken = default)
    {
        ThrowIfGroupNodeId(nodeId);
        return _connection.SendRequestAsync("remove_node", writer => writer.WriteNumber("node_id", nodeId), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<MatterCommissioningWindow> OpenCommissioningWindowAsync(ulong nodeId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ThrowIfGroupNodeId(nodeId);
        if (timeout < MinCommissioningWindowTimeout || timeout > MaxCommissioningWindowTimeout || timeout.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "A commissioning window timeout is a whole number of seconds from 180 to 900.");
        }

        var seconds = (int)timeout.TotalSeconds;
        var result = await _connection.SendRequestAsync("open_commissioning_window", writer =>
        {
            writer.WriteNumber("node_id", nodeId);
            writer.WriteNumber("timeout", seconds);
        }, cancellationToken).ConfigureAwait(false);

        return MatterServerResults.ReadCommissioningWindow(result);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        _events.Writer.TryComplete();
    }

    private void HandleEvent(MatterControllerEvent controllerEvent)
    {
        if (controllerEvent is MatterServerInfoUpdatedEvent serverInfoChanged)
        {
            _serverInfo = serverInfoChanged.ServerInfo;
        }

        _events.Writer.TryWrite(controllerEvent);
    }

    private void HandleConnectionLost(Exception exception) => _events.Writer.TryComplete(exception);

    private static void ThrowIfGroupNodeId(ulong nodeId)
    {
        if (nodeId >= FirstGroupNodeId)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeId), nodeId, "Group node IDs (0xFFFFFFFFFFFF0000 and above) are not supported.");
        }
    }
}
