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
    /// <summary>The oldest server schema version this client works with.</summary>
    public const int MinimumServerSchemaVersion = 14;

    /// <summary>The schema version this client implements.</summary>
    public const int ClientSchemaVersion = 15;

    private static readonly TimeSpan MaxTimedRequestTimeout = TimeSpan.FromMilliseconds(ushort.MaxValue);

    private readonly MatterServerConnection _connection;
    private readonly TimeSpan _requestTimeout;
    private readonly Channel<MatterControllerEvent> _events = Channel.CreateUnbounded<MatterControllerEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private volatile MatterServerInfo _serverInfo;

    private MatterServerController(MatterServerConnection connection, MatterServerInfo serverInfo, TimeSpan requestTimeout)
    {
        _connection = connection;
        _serverInfo = serverInfo;
        _requestTimeout = requestTimeout;
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
    /// <exception cref="NotSupportedException">The server's schema version is not supported.</exception>
    /// <exception cref="TimeoutException">The server sent no information within <see cref="MatterServerOptions.RequestTimeout"/>.</exception>
    public static async Task<MatterServerController> ConnectAsync(
        Uri serverUrl,
        MatterServerOptions? options = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverUrl);
        options ??= new MatterServerOptions();
        var connection = await MatterServerConnection.ConnectAsync(
            serverUrl, options.RequestTimeout, options.MaxMessageSize, logger, cancellationToken).ConfigureAwait(false);
        try
        {
            var serverInfo = await connection.ReceiveServerInfoAsync(cancellationToken).ConfigureAwait(false);
            MatterServerJson.ThrowIfUnsupported(serverInfo);
            var controller = new MatterServerController(connection, serverInfo, options.RequestTimeout);
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
        var result = await _connection.SendRequestAsync("start_listening", null, _requestTimeout, cancellationToken).ConfigureAwait(false);
        return MatterServerJson.ReadNodes(result);
    }

    /// <inheritdoc />
    public async Task WriteAttributeAsync(ulong nodeId, AttributePath path, IMatterPayload value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        var result = await _connection.SendRequestAsync("write_attribute", writer =>
        {
            writer.WriteNumber("node_id", nodeId);
            writer.WriteString("attribute_path", path.ToString());
            JsonMatterValueWriter.WriteProperty(writer, "value", value);
        }, _requestTimeout, cancellationToken).ConfigureAwait(false);

        MatterServerJson.ThrowIfWriteFailed(result, nodeId, path);
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
        }, _requestTimeout, cancellationToken).ConfigureAwait(false);

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
        }, timeout: null, cancellationToken).ConfigureAwait(false);

        return MatterServerJson.ReadNode(result);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MatterOnboardingPayload>> ParseOnboardingPayloadAsync(string code, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        var result = await _connection.SendRequestAsync("parse_onboarding_payload", writer => writer.WriteString("code", code), _requestTimeout, cancellationToken)
            .ConfigureAwait(false);
        return MatterServerJson.ReadOnboardingPayloads(result);
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
        }, _requestTimeout, cancellationToken);
    }

    /// <inheritdoc />
    public Task SetThreadDatasetAsync(string dataset, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataset);
        return _connection.SendRequestAsync("set_thread_dataset", writer => writer.WriteString("dataset", dataset), _requestTimeout, cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveNodeAsync(ulong nodeId, CancellationToken cancellationToken = default) =>
        _connection.SendRequestAsync("remove_node", writer => writer.WriteNumber("node_id", nodeId), _requestTimeout, cancellationToken);

    /// <inheritdoc />
    public async Task<MatterCommissioningWindow> OpenCommissioningWindowAsync(ulong nodeId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var result = await _connection.SendRequestAsync("open_commissioning_window", writer =>
        {
            writer.WriteNumber("node_id", nodeId);
            writer.WriteNumber("timeout", (int)timeout.TotalSeconds);
        }, _requestTimeout, cancellationToken).ConfigureAwait(false);

        return MatterServerJson.ReadCommissioningWindow(result);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        _events.Writer.TryComplete();
    }

    private bool HandleEvent(string name, JsonElement data)
    {
        var controllerEvent = MatterServerJson.ReadEvent(name, data);
        if (controllerEvent is MatterServerInfoChangedEvent serverInfoChanged)
        {
            _serverInfo = serverInfoChanged.ServerInfo;
        }

        return controllerEvent is not null && _events.Writer.TryWrite(controllerEvent);
    }

    private void HandleConnectionLost(Exception exception) => _events.Writer.TryComplete(exception);
}
