using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Reactive.Concurrency;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Attributes;
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

    [Fact]
    public async Task WhenABroadcastIsSlicedBeforeTheChangeAttachingANewSubject_ThenTheFirstSliceCompletesTheSubject()
    {
        // Arrange
        var serverContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var existingItem = new TestItem(serverContext) { Label = "Existing", Value = 1 };
        var serverRoot = new TestRoot(serverContext) { Name = "Root", Items = [existingItem] };

        var handler = new WebSocketSubjectHandler(serverRoot, new WebSocketServerConfiguration { WriteBatchSize = 1 }, NullLogger.Instance);
        var socket = new CapturingWebSocket();
        socket.EnqueueIncoming(_serializer.SerializeMessage(MessageType.Hello, new HelloPayload()));

        using var cancellation = new CancellationTokenSource();
        var clientTask = handler.HandleClientAsync(socket, cancellation.Token);

        await AsyncTestHelpers.WaitUntilAsync(
            () => socket.TryGetMessage(MessageType.Welcome, _serializer, out _),
            message: "Server should send the Welcome message");

        // Act: the new item carries the context, so its value change is captured before the change attaching it
        var newItem = new TestItem(serverContext) { Label = "New", Value = 2 };
        var changes = new List<SubjectPropertyChange>();
        using (serverContext.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add))
        {
            newItem.Label = "Renamed";
            serverRoot.Items = [existingItem, newItem];
        }

        await handler.BroadcastChangesAsync(changes.ToArray(), CancellationToken.None);

        // Assert
        var firstUpdate = DeserializePayload<UpdatePayload>(socket.GetMessages(MessageType.Update, _serializer).First());
        Assert.Contains(newItem.TryGetSubjectId()!, firstUpdate.CompleteSubjectIds!);

        await cancellation.CancelAsync();
        await clientTask;
    }

    [Fact]
    public async Task WhenABroadcastIsSlicedAndANewSubjectHoldsANewChild_ThenTheFirstUpdateCarriesBoth()
    {
        // Arrange
        var serverContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var serverRoot = new BroadcastNode(serverContext) { Name = "Root" };
        await using var session = await BroadcastSession.StartAsync(serverRoot, _serializer);

        // Act: the new subject carries the context, so its child's assignment is captured before its own attach
        var parent = new BroadcastNode(serverContext) { Name = "Parent" };
        var changes = CaptureChanges(serverContext, () =>
        {
            parent.Child = new BroadcastNode { Name = "Child" };
            serverRoot.Child = parent;
        });
        await session.Handler.BroadcastChangesAsync(changes, CancellationToken.None);

        // Assert
        session.ApplyUpdates(count: 1);
        Assert.Equal("Child", session.ClientRoot.Child?.Child?.Name);
    }

    [Fact]
    public async Task WhenABroadcastIsSlicedAndASubjectMovesBetweenParents_ThenTheClientKeepsItsInstance()
    {
        // Arrange
        var serverContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var moved = new BroadcastNode { Name = "Moved" };
        var firstParent = new BroadcastNode { Name = "First", Items = [moved] };
        var secondParent = new BroadcastNode { Name = "Second" };
        var serverRoot = new BroadcastNode(serverContext) { Name = "Root", Items = [firstParent, secondParent] };
        await using var session = await BroadcastSession.StartAsync(serverRoot, _serializer);
        var clientMoved = Assert.Single(session.ClientRoot.Items[0].Items);

        // Act
        var changes = CaptureChanges(serverContext, () =>
        {
            firstParent.Items = [];
            secondParent.Items = [moved];
        });
        await session.Handler.BroadcastChangesAsync(changes, CancellationToken.None);

        // Assert
        session.ApplyUpdates(count: null);
        Assert.Empty(session.ClientRoot.Items[0].Items);
        Assert.Same(clientMoved, Assert.Single(session.ClientRoot.Items[1].Items));
    }

    private static SubjectPropertyChange[] CaptureChanges(IInterceptorSubjectContext context, Action change)
    {
        var changes = new List<SubjectPropertyChange>();
        using (context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add))
        {
            change();
        }

        return changes.ToArray();
    }

    /// <summary>
    /// A server handler broadcasting one change per update to a connected client whose graph starts from the
    /// Welcome snapshot and applies received updates on request.
    /// </summary>
    private sealed class BroadcastSession : IAsyncDisposable
    {
        private readonly CapturingWebSocket _socket;
        private readonly JsonWebSocketSerializer _serializer;
        private readonly CancellationTokenSource _cancellation = new();
        private Task _clientTask = Task.CompletedTask;
        private int _appliedUpdateCount;

        private BroadcastSession(WebSocketSubjectHandler handler, CapturingWebSocket socket, JsonWebSocketSerializer serializer)
        {
            Handler = handler;
            _socket = socket;
            _serializer = serializer;
        }

        public WebSocketSubjectHandler Handler { get; }

        public BroadcastNode ClientRoot { get; } = new(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry());

        public static async Task<BroadcastSession> StartAsync(BroadcastNode serverRoot, JsonWebSocketSerializer serializer)
        {
            var socket = new CapturingWebSocket();
            socket.EnqueueIncoming(serializer.SerializeMessage(MessageType.Hello, new HelloPayload()));
            var session = new BroadcastSession(
                new WebSocketSubjectHandler(serverRoot, new WebSocketServerConfiguration { WriteBatchSize = 1 }, NullLogger.Instance),
                socket,
                serializer);
            session._clientTask = session.Handler.HandleClientAsync(socket, session._cancellation.Token);

            await AsyncTestHelpers.WaitUntilAsync(
                () => socket.TryGetMessage(MessageType.Welcome, serializer, out _),
                message: "Server should send the Welcome message");
            socket.TryGetMessage(MessageType.Welcome, serializer, out var welcome);
            session.ClientRoot.ApplySubjectUpdate(session.Deserialize<WelcomePayload>(welcome).State!, DefaultSubjectFactory.Instance, ChangeOrigin.Local);
            return session;
        }

        /// <summary>Applies the next <paramref name="count"/> received updates, or all of them.</summary>
        public void ApplyUpdates(int? count)
        {
            var updates = _socket.GetMessages(MessageType.Update, _serializer).Skip(_appliedUpdateCount).ToArray();
            foreach (var update in updates.Take(count ?? updates.Length))
            {
                ClientRoot.ApplySubjectUpdate(Deserialize<UpdatePayload>(update), DefaultSubjectFactory.Instance, ChangeOrigin.Local);
                _appliedUpdateCount++;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();
            await _clientTask;
            _cancellation.Dispose();
        }

        private T Deserialize<T>(byte[] message)
        {
            var (_, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(message);
            return _serializer.Deserialize<T>(message.AsSpan(payloadStart, payloadLength));
        }
    }

    private T DeserializePayload<T>(byte[] message)
    {
        var (_, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(message);
        return _serializer.Deserialize<T>(message.AsSpan(payloadStart, payloadLength));
    }
}

/// <summary>
/// A WebSocket that hands the handler a queued inbound message, parks on every further receive
/// until cancellation, and keeps every message the server sends for inspection.
/// </summary>
internal sealed class CapturingWebSocket : System.Net.WebSockets.WebSocket
{
    private readonly ConcurrentQueue<byte[]> _incoming = new();
    private readonly ConcurrentQueue<byte[]> _sent = new();

    public void EnqueueIncoming(byte[] message) => _incoming.Enqueue(message);

    public IEnumerable<byte[]> GetMessages(MessageType messageType, IWebSocketSerializer serializer)
        => _sent.ToArray().Where(sent => serializer.DeserializeMessageEnvelope(sent).Type == messageType);

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

/// <summary>A subject that nests subjects of its own type, for broadcasts that attach or move a subtree.</summary>
[InterceptorSubject]
public partial class BroadcastNode
{
    public BroadcastNode()
    {
        Name = "";
        Items = [];
    }

    public partial string Name { get; set; }

    public partial BroadcastNode? Child { get; set; }

    public partial List<BroadcastNode> Items { get; set; }
}
