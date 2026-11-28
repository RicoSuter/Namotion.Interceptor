using System.Threading.Channels;

namespace Namotion.Matter;

/// <summary>
/// A connection to a Matter controller. Requests can be sent concurrently. A failed request throws
/// <see cref="MatterException"/> and one without an answer in time <see cref="TimeoutException"/>. Once the connection
/// is lost, pending and later requests throw an exception whose <see cref="Exception.InnerException"/> is the error that
/// closed the connection; once the controller is disposed, they throw <see cref="ObjectDisposedException"/>.
/// </summary>
public interface IMatterController : IAsyncDisposable
{
    /// <summary>Gets the controller's current information.</summary>
    MatterServerInfo ServerInfo { get; }

    /// <summary>
    /// Gets the events in the order the controller observed them, after <see cref="StartListeningAsync"/>. The channel
    /// is unbounded, so the consumer must keep reading it. It completes with the error that closed the connection, or
    /// without an error when the controller is disposed.
    /// </summary>
    ChannelReader<MatterControllerEvent> Events { get; }

    /// <summary>Gets all commissioned nodes and starts delivering <see cref="Events"/>.</summary>
    Task<IReadOnlyList<MatterNode>> StartListeningAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes an attribute.</summary>
    /// <exception cref="MatterException">The device rejected the write; <see cref="MatterException.Status"/> says why.</exception>
    Task WriteAttributeAsync(ulong nodeId, AttributePath path, IMatterPayload value, CancellationToken cancellationToken = default);

    /// <summary>Invokes a command.</summary>
    /// <param name="nodeId">The node ID.</param>
    /// <param name="path">The command path.</param>
    /// <param name="commandName">The command's name as the server spells it, in lower camel case.</param>
    /// <param name="payload">The command's fields, or null for a command without fields.</param>
    /// <param name="timedRequestTimeout">The timeout of a timed invoke, which some commands such as door lock commands require, from 0 to 65535 milliseconds.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The command response, or null for a command without response.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timedRequestTimeout"/> is outside 0 to 65535 milliseconds.</exception>
    Task<MatterValue> InvokeAsync(
        ulong nodeId,
        CommandPath path,
        string commandName,
        IMatterPayload? payload = null,
        TimeSpan? timedRequestTimeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Commissions a device with a QR code payload or manual pairing code. The request has no timeout; canceling it
    /// stops waiting, and the node then still arrives as <see cref="MatterNodeAddedEvent"/> if commissioning succeeds.
    /// </summary>
    /// <param name="code">The QR code payload (<c>MT:...</c>) or manual pairing code.</param>
    /// <param name="networkOnly">Whether to find the device only on the IP network, without Bluetooth.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The commissioned node.</returns>
    Task<MatterNode> CommissionAsync(string code, bool networkOnly, CancellationToken cancellationToken = default);

    /// <summary>Decodes a QR code payload or manual pairing code without contacting a device.</summary>
    /// <returns>One payload per encoded device; a manual code encodes one.</returns>
    Task<IReadOnlyList<MatterOnboardingPayload>> ParseOnboardingPayloadAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>Sets the Wi-Fi credentials sent to Wi-Fi devices during commissioning. The password is never returned.</summary>
    Task SetWifiCredentialsAsync(string ssid, string password, CancellationToken cancellationToken = default);

    /// <summary>Sets the Thread operational dataset, as hex TLV, sent to Thread devices during commissioning.</summary>
    Task SetThreadDatasetAsync(string dataset, CancellationToken cancellationToken = default);

    /// <summary>Decommissions and removes a node.</summary>
    Task RemoveNodeAsync(ulong nodeId, CancellationToken cancellationToken = default);

    /// <summary>Opens a commissioning window so another controller can commission the node.</summary>
    Task<MatterCommissioningWindow> OpenCommissioningWindowAsync(ulong nodeId, TimeSpan timeout, CancellationToken cancellationToken = default);
}
