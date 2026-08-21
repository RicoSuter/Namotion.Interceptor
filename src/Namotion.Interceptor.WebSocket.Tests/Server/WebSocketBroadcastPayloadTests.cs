using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Reactive.Concurrency;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.WebSocket.Protocol;
using Namotion.Interceptor.WebSocket.Serialization;
using Namotion.Interceptor.WebSocket.Server;
using Namotion.Interceptor.WebSocket.Tests.Integration;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Server;

/// <summary>
/// Drives the real server broadcast path (change to partial update to payload to serializer to
/// socket) against a connected client, and applies the received bytes to a receiving subject graph.
/// The payload is a <see cref="UpdatePayload"/> built from a <see cref="SubjectUpdate"/>, so anything
/// the server fails to carry over is invisible until it is missing on the wire, which is what these
/// tests inspect.
/// </summary>
public class WebSocketBroadcastPayloadTests
{
    private readonly JsonWebSocketSerializer _serializer = JsonWebSocketSerializer.Instance;

    [Fact]
    public async Task WhenServerBroadcastsPartialUpdateWithNewSubject_ThenCompleteSubjectIdsReachTheClient()
    {
        // Arrange
        var serverContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var existingItem = new TestItem(serverContext) { Label = "Existing", Value = 1 };
        var serverRoot = new TestRoot(serverContext) { Name = "Root", Items = [existingItem] };

        var handler = new WebSocketSubjectHandler(serverRoot, new WebSocketServerConfiguration(), NullLogger.Instance);
        var socket = new CapturingWebSocket();
        socket.EnqueueIncoming(_serializer.SerializeMessage(MessageType.Hello, new HelloPayload()));

        using var cancellation = new CancellationTokenSource();
        var clientTask = handler.HandleClientAsync(socket, cancellation.Token);

        await AsyncTestHelpers.WaitUntilAsync(
            () => socket.TryGetMessage(MessageType.Welcome, _serializer, out _),
            message: "Server should send the Welcome message");

        Assert.True(socket.TryGetMessage(MessageType.Welcome, _serializer, out var welcomeBytes));
        var welcome = DeserializePayload<WelcomePayload>(welcomeBytes);

        var clientContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var clientRoot = new TestRoot(clientContext);
        clientRoot.ApplySubjectUpdate(welcome.State!, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Act - add an item on the server and broadcast the resulting changes
        var newItem = new TestItem(serverContext) { Label = "New", Value = 2 };
        var changes = new List<SubjectPropertyChange>();
        using (serverContext.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add))
        {
            serverRoot.Items = [existingItem, newItem];
        }

        await handler.BroadcastChangesAsync(changes.ToArray(), CancellationToken.None);

        // Assert
        Assert.True(socket.TryGetMessage(MessageType.Update, _serializer, out var updateBytes),
            "Server should have broadcast an Update message");

        var receivedUpdate = DeserializePayload<UpdatePayload>(updateBytes);

        Assert.NotNull(receivedUpdate.CompleteSubjectIds);
        Assert.Contains(newItem.TryGetSubjectId()!, receivedUpdate.CompleteSubjectIds!);
        Assert.DoesNotContain(existingItem.TryGetSubjectId()!, receivedUpdate.CompleteSubjectIds!);

        clientRoot.ApplySubjectUpdate(receivedUpdate, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        Assert.Equal(2, clientRoot.Items.Length);
        Assert.Equal("New", clientRoot.Items[1].Label);
        Assert.Equal(2, clientRoot.Items[1].Value);

        await cancellation.CancelAsync();
        await clientTask;
    }

    [Fact]
    public async Task WhenBroadcastUpdateOnlyReordersKnownItems_ThenAReceiverThatCannotResolveThemCreatesNoSubjects()
    {
        // Arrange
        var serverContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var firstItem = new TestItem(serverContext) { Label = "First", Value = 1 };
        var secondItem = new TestItem(serverContext) { Label = "Second", Value = 2 };
        var serverRoot = new TestRoot(serverContext) { Name = "Root", Items = [firstItem, secondItem] };

        var handler = new WebSocketSubjectHandler(serverRoot, new WebSocketServerConfiguration(), NullLogger.Instance);
        var socket = new CapturingWebSocket();
        socket.EnqueueIncoming(_serializer.SerializeMessage(MessageType.Hello, new HelloPayload()));

        using var cancellation = new CancellationTokenSource();
        var clientTask = handler.HandleClientAsync(socket, cancellation.Token);

        await AsyncTestHelpers.WaitUntilAsync(
            () => socket.TryGetMessage(MessageType.Welcome, _serializer, out _),
            message: "Server should send the Welcome message");

        // Act - a reorder introduces no new subject, so it marks nothing complete
        var changes = new List<SubjectPropertyChange>();
        using (serverContext.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add))
        {
            serverRoot.Items = [secondItem, firstItem];
        }

        await handler.BroadcastChangesAsync(changes.ToArray(), CancellationToken.None);

        Assert.True(socket.TryGetMessage(MessageType.Update, _serializer, out var updateBytes),
            "Server should have broadcast an Update message");

        var receivedUpdate = DeserializePayload<UpdatePayload>(updateBytes);

        // A receiver that never learned the two item IDs, which is the state of any receiver that
        // missed the update carrying their complete state.
        var clientContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var clientRoot = new TestRoot(clientContext);
        clientRoot.ApplySubjectUpdate(receivedUpdate, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert - without the complete set on the wire the receiver treats every referenced ID as
        // complete and fabricates two default-valued items that no later update ever repairs.
        Assert.NotNull(receivedUpdate.CompleteSubjectIds);
        Assert.Empty(receivedUpdate.CompleteSubjectIds!);
        Assert.Empty(clientRoot.Items);

        var clientRegistry = clientContext.GetService<ISubjectRegistry>();
        var registeredSubject = Assert.Single(clientRegistry.KnownSubjects).Key;
        Assert.Same(clientRoot, registeredSubject);

        await cancellation.CancelAsync();
        await clientTask;
    }

    private T DeserializePayload<T>(byte[] message)
    {
        var (_, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(message);
        return _serializer.Deserialize<T>(message.AsSpan(payloadStart, payloadLength));
    }

    /// <summary>
    /// A WebSocket that hands the handler a queued inbound message, parks on every further receive
    /// until cancellation, and keeps every message the server sends for inspection.
    /// </summary>
    private sealed class CapturingWebSocket : System.Net.WebSockets.WebSocket
    {
        private readonly ConcurrentQueue<byte[]> _incoming = new();
        private readonly ConcurrentQueue<byte[]> _sent = new();

        public void EnqueueIncoming(byte[] message) => _incoming.Enqueue(message);

        public bool TryGetMessage(MessageType messageType, IWebSocketSerializer serializer, out byte[] message)
        {
            foreach (var sent in _sent.ToArray())
            {
                if (serializer.DeserializeMessageEnvelope(sent).Type == messageType)
                {
                    message = sent;
                    return true;
                }
            }

            message = [];
            return false;
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_incoming.TryDequeue(out var message))
            {
                var count = Math.Min(message.Length, buffer.Count);
                message.AsSpan(0, count).CopyTo(buffer.AsSpan());
                return new WebSocketReceiveResult(count, WebSocketMessageType.Text, true);
            }

            var parked = new TaskCompletionSource<WebSocketReceiveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registration = cancellationToken.Register(() => parked.TrySetCanceled(cancellationToken));
            return await parked.Task.ConfigureAwait(false);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            _sent.Enqueue(buffer.ToArray());
            return Task.CompletedTask;
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort()
        {
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override void Dispose()
        {
        }
    }
}
