using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.IO;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Resilience;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.WebSocket.Internal;
using Namotion.Interceptor.WebSocket.Protocol;
using Namotion.Interceptor.WebSocket.Serialization;

namespace Namotion.Interceptor.WebSocket.Client;

/// <summary>
/// WebSocket client source that connects to a WebSocket server and synchronizes subjects.
/// </summary>
public sealed class WebSocketSubjectClientSource : SubjectSourceBase, IFaultInjectable, IAsyncDisposable
{
    private const int SendBufferShrinkThreshold = 256 * 1024;

    private static RecyclableMemoryStreamManager MemoryStreamManager => WebSocketMessageReader.MemoryStreamManager;

    private readonly IInterceptorSubject _subject;
    private readonly WebSocketClientConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly ISubjectUpdateProcessor[] _processors;
    private readonly IWebSocketSerializer _serializer = JsonWebSocketSerializer.Instance;
    private ArrayBufferWriter<byte> _sendBuffer = new(4096);

    private volatile ClientWebSocket? _webSocket;
    private volatile SubjectPropertyWriter? _propertyWriter;
    private volatile SubjectUpdate? _initialState;
    private volatile CancellationTokenSource? _receiveCts;
    private volatile TaskCompletionSource? _receiveLoopCompleted;
    private TaskCompletionSource? _receiveLoopLivenessOwner;
    private ConnectorCommitLease? _receiveLoopCommitLease;
    private readonly SourceOwnershipManager _ownership;
    private readonly CircuitBreaker? _circuitBreaker;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    // A receive loop may finish while its replacement connects. Keep owner publication and both
    // liveness transitions ordered without putting a lock on the message receive path.
    private readonly Lock _receiveLoopLivenessLock = new();

    // Changes handed to SendAsync on the current connection, collapsed per property. A successful send
    // is not an acknowledgement, so these are re-parked on the next connect, where the reconcile decides
    // whether each is sent, restored or dropped. Guarded by its own lock rather than by
    // _connectionLock, which a send holds across an untimed SendAsync: the receive loop retires from
    // here and must not be able to wedge behind a stalled send.
    private readonly Lock _inFlightLock = new();
    private readonly Dictionary<PropertyReference, (long Ordinal, SubjectPropertyChange Change)> _inFlight = new(PropertyReference.Comparer);
    private long _sendOrdinal;

    // Read by WriteChangesAsync under _connectionLock, the same section that reads _webSocket, and by
    // the receive loop's heartbeat handling on its own thread, so it is volatile rather than merely
    // lock-guarded. Written only in ConnectCoreAsync, which every connection attempt passes through
    // before the socket it sets is usable, so a write always precedes any read that could see it.
    private volatile bool _acknowledgesAppliedUpdates;

    // Guards the once-per-change warning below. Read and written only inside ConnectCoreAsync, which
    // _connectionLock serializes across every connection attempt, so a plain field is enough.
    private bool? _lastLoggedAcknowledgesAppliedUpdates;

    private int _disposed;

    /// <inheritdoc />
    public override IInterceptorSubject RootSubject => _subject;

    /// <inheritdoc />
    public override int WriteBatchSize => _configuration.WriteBatchSize;

    public WebSocketSubjectClientSource(
        IInterceptorSubject subject,
        WebSocketClientConfiguration configuration,
        ILogger<WebSocketSubjectClientSource> logger)
        : base(
            (subject ?? throw new ArgumentNullException(nameof(subject))).Context,
            logger ?? throw new ArgumentNullException(nameof(logger)),
            (configuration ?? throw new ArgumentNullException(nameof(configuration))).BufferTime,
            configuration.RetryTime,
            configuration.WriteRetryQueueSize,
            configuration.TeardownFlushTimeout)
    {
        _subject = subject;
        _configuration = configuration;
        _logger = logger;
        _processors = configuration.Processors;
        _ownership = new SourceOwnershipManager(this);

        Metrics.RegisterClaimedProperties(() => _ownership.Count);

        if (configuration.CircuitBreakerFailureThreshold > 0)
        {
            _circuitBreaker = new CircuitBreaker(
                configuration.CircuitBreakerFailureThreshold,
                configuration.CircuitBreakerCooldown);
        }

        configuration.Validate();
    }

    internal SourceOwnershipManager Ownership => _ownership;

    /// <inheritdoc />
    protected override async Task<IAsyncDisposable?> StartListeningAsync(SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken)
    {
        _propertyWriter = propertyWriter;

        try
        {
            await ConnectAsync(cancellationToken).ConfigureAwait(false);

            return BackgroundTaskLifetime.Start(
                cancellationToken,
                _logger,
                RunMonitorLoopAsync,
                DisposeWebSocketConnectionAsync);
        }
        catch
        {
            await StopReceiveLoopAndDisposeSocketAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask DisposeWebSocketConnectionAsync()
    {
        await RetireReceiveLoopCommitsAsync().ConfigureAwait(false);

        var currentSocket = _webSocket;
        if (currentSocket?.State == WebSocketState.Open)
        {
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await currentSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closing", closeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                currentSocket.Abort();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while closing WebSocket connection");
            }
        }

        await StopReceiveLoopAndDisposeSocketAsync().ConfigureAwait(false);
    }

    private async Task StopReceiveLoopAndDisposeSocketAsync()
    {
        await RetireReceiveLoopCommitsAsync().ConfigureAwait(false);

        var receiveCts = _receiveCts;
        var webSocket = _webSocket;

        // Gated on there being anything left to tear down here, not merely on the in-flight count: the
        // belt-and-braces call from DisposeAsync finds both fields already null and would otherwise
        // repeat this log for the same writes a prior call already reported.
        var hadTeardownWork = receiveCts is not null || webSocket is not null;

        if (receiveCts is not null)
        {
            try { await receiveCts.CancelAsync().ConfigureAwait(false); } catch { /* ignore */ }

            var receiveLoop = _receiveLoopCompleted?.Task;
            if (receiveLoop is not null && !receiveLoop.IsCompleted)
            {
                await WaitForReceiveLoopExitAsync(receiveLoop).ConfigureAwait(false);
            }

            try { receiveCts.Dispose(); } catch { /* ignore */ }
            _receiveCts = null;
        }

        if (hadTeardownWork)
        {
            // Read after the receive loop has exited, not before: a retire that lands on the loop's
            // own thread during the wait above must be reflected here, or this overstates what
            // genuinely survived teardown.
            var outstanding = InFlightCount;
            if (outstanding > 0)
            {
                // Not counted as a drop: these reached the socket and may already have been applied, so
                // treating them as lost here would inflate the drop metric on every clean shutdown.
                _logger.LogWarning(
                    "Ending this connection with {Count} write(s) that reached the socket but were never confirmed applied. " +
                    "They remain queued and will be re-asserted on the next connection.",
                    outstanding);
            }
        }

        if (webSocket is not null)
        {
            try { webSocket.Abort(); } catch { /* ignore */ }
            try { webSocket.Dispose(); } catch { /* ignore */ }
            _webSocket = null;
        }
    }

    private async Task WaitForReceiveLoopExitAsync(Task receiveLoop)
    {
        try
        {
            using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await receiveLoop.WaitAsync(waitCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A loop that outlives this timeout is already excluded from committing: its lease was
            // retired before the wait started, so cleanup can proceed without it.
            _logger.LogWarning("Receive loop did not complete within timeout, continuing cleanup");
        }
    }

    /// <summary>
    /// Retires the current commit lease and waits for every admitted commit to finish.
    /// </summary>
    /// <remarks>
    /// The drain is deliberately unbounded, with no timeout or token, and disposal also passes
    /// through here after its capped stop has already returned: a commit that is already inside
    /// <c>ApplySubjectUpdate</c> must not land after a replacement connection has loaded its state,
    /// and capping the wait would reopen exactly that race. The cost is that teardown can block for
    /// as long as a single property commit takes.
    /// </remarks>
    private async ValueTask RetireReceiveLoopCommitsAsync()
    {
        var commitLease = Volatile.Read(ref _receiveLoopCommitLease);
        if (commitLease is null)
        {
            return;
        }

        var retirementTask = commitLease.RetireAsync();
        Interlocked.CompareExchange(ref _receiveLoopCommitLease, null, commitLease);
        await retirementTask.ConfigureAwait(false);
        AfterReceiveLoopCommitDrain?.Invoke();
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        // Every path that establishes a connection passes here, so this is the single re-park site.
        // The resume gate holds these in the queue until the reconcile has judged them against the
        // state the new connection is about to deliver.
        ReParkInFlightChanges();

        await RetireReceiveLoopCommitsAsync().ConfigureAwait(false);

        // Clean up any previous connection
        if (_receiveCts is not null)
        {
            await _receiveCts.CancelAsync().ConfigureAwait(false);

            // Wait for receive loop to exit before disposing socket
            var previousReceiveLoop = _receiveLoopCompleted?.Task;
            if (previousReceiveLoop is not null)
            {
                await WaitForReceiveLoopExitAsync(previousReceiveLoop).ConfigureAwait(false);
            }

            var oldCts = _receiveCts;
            _receiveCts = null;
            oldCts.Dispose();
        }

        // Now safe to dispose socket
        _webSocket?.Dispose();

        var receiveLoopStarted = false;
        var receiveCancellation = new CancellationTokenSource();
        var receiveLoopCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiveLoopCommitLease = new ConnectorCommitLease();

        // Per attempt rather than shared: a tracker surviving a reconnect would validate the new
        // connection's sequence numbers against the old connection's position.
        var sequenceTracker = new ClientSequenceTracker();
        _receiveCts = receiveCancellation;
        _receiveLoopCompleted = receiveLoopCompletion;

        try
        {
            _webSocket = new ClientWebSocket();

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(_configuration.ConnectTimeout);

            _logger.LogInformation("Connecting to WebSocket server at {Uri}", _configuration.ServerUri);
            await _webSocket.ConnectAsync(_configuration.ServerUri!, connectCts.Token).ConfigureAwait(false);

            // Send Hello using reusable buffer
            var hello = new HelloPayload { Format = WebSocketFormat.Json };
            _sendBuffer.Clear();
            _serializer.SerializeMessageTo(_sendBuffer, MessageType.Hello, hello);
            await _webSocket.SendAsync(_sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);

            // Receive Welcome using shared utility
            using var readResult = await WebSocketMessageReader.ReadMessageAsync(
                _webSocket, _configuration.MaxMessageSize, cancellationToken).ConfigureAwait(false);

            if (readResult.IsCloseMessage)
            {
                throw new InvalidOperationException("Server closed connection during handshake");
            }

            if (readResult.ExceededMaxSize)
            {
                throw new InvalidOperationException($"Message exceeds maximum size of {_configuration.MaxMessageSize} bytes");
            }

            if (!readResult.Success)
            {
                throw new InvalidOperationException("Failed to receive Welcome message");
            }

            var (messageType, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(readResult.MessageBytes.Span);
            if (messageType == MessageType.Error)
            {
                var error = _serializer.Deserialize<ErrorPayload>(readResult.MessageBytes.Span.Slice(payloadStart, payloadLength));
                throw new InvalidOperationException($"Server returned error during handshake: [{error.Code}] {error.Message}");
            }

            if (messageType != MessageType.Welcome)
            {
                throw new InvalidOperationException($"Expected Welcome message, got {messageType}");
            }

            var welcome = _serializer.Deserialize<WelcomePayload>(readResult.MessageBytes.Span.Slice(payloadStart, payloadLength));
            if (welcome.Version != WebSocketProtocol.Version)
            {
                throw new InvalidOperationException($"Unsupported server protocol version {welcome.Version}. Client supports version {WebSocketProtocol.Version}.");
            }

            _initialState = welcome.State;
            sequenceTracker.InitializeFromWelcome(welcome.Sequence);

            // Stored for this connection attempt: read again on every WriteChangesAsync call while this
            // connection lasts, and re-read here on every reconnect since a peer can change its mind
            // between connections (a server restarted with heartbeats toggled).
            RecordAcknowledgementMode(welcome.AcknowledgesAppliedUpdates);

            Volatile.Write(ref _receiveLoopCommitLease, receiveLoopCommitLease);
            PublishReceiveLoopAndMarkOperational(receiveLoopCompletion);
            _logger.LogInformation("Connected to WebSocket server (sequence: {Sequence})", welcome.Sequence);

            // Start receive loop (signals _receiveLoopCompleted when done)
            _ = ReceiveLoopAsync(receiveCancellation.Token, receiveLoopCompletion, receiveLoopCommitLease, sequenceTracker);
            receiveLoopStarted = true;
        }
        finally
        {
            if (!receiveLoopStarted)
            {
                MarkNotOperationalAfterFailedConnection();

                // Dispose the socket to avoid holding resources during backoff delay
                _webSocket?.Dispose();
                _webSocket = null;

                // Signal completion to prevent the monitor loop from hanging
                receiveLoopCompletion.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Stores whether the server this connection just reached acknowledges applied updates on it, and
    /// warns once when the no-acknowledgement mode is entered or re-entered.
    /// </summary>
    /// <param name="acknowledgesAppliedUpdates">The capability the Welcome from this connect reported.</param>
    /// <remarks>
    /// Logged at most once per change, not once per reconnect: with heartbeats disabled the receive
    /// timeout treats silence as connection loss, so an idle client reconnects roughly once a minute,
    /// and a per-reconnect warning here would fire forever at that cadence. Entering the acknowledging
    /// mode is not itself warned about, since it is the safe side and needs no operator attention; only
    /// the transition into, or back into, the mode where losses cannot be repaired is worth a warning.
    /// </remarks>
    private void RecordAcknowledgementMode(bool acknowledgesAppliedUpdates)
    {
        if (!acknowledgesAppliedUpdates && _lastLoggedAcknowledgesAppliedUpdates != false)
        {
            _logger.LogWarning(
                "Server does not acknowledge applied writes on this connection. Writes that reach the " +
                "socket but are not confirmed applied before a disconnect will not be re-asserted after " +
                "the next reconnect.");
        }

        _lastLoggedAcknowledgesAppliedUpdates = acknowledgesAppliedUpdates;
        _acknowledgesAppliedUpdates = acknowledgesAppliedUpdates;
    }

    private void ReParkInFlightChanges()
    {
        SubjectPropertyChange[] outstanding;
        lock (_inFlightLock)
        {
            if (_inFlight.Count == 0)
            {
                _sendOrdinal = 0;
                return;
            }

            outstanding = new SubjectPropertyChange[_inFlight.Count];
            var index = 0;
            foreach (var entry in _inFlight.Values)
            {
                outstanding[index++] = entry.Change;
            }

            _inFlight.Clear();
            _sendOrdinal = 0;
        }

        _logger.LogInformation(
            "Re-asserting {Count} write(s) that reached the socket but were never acknowledged.", outstanding.Length);

        // Inserted at the front: these predate everything the gate has already parked during this
        // outage, so appending them at the back would rank them as the newest entries and let the
        // ring buffer evict the genuinely newer outage writes ahead of them.
        //
        // outstanding is already one entry per property by construction, so ParkChangesForRetry's own
        // collapse is a no-op here; paying for it again is cheaper than a second entry point to skip it.
        ParkChangesForRetry(outstanding);
    }

    /// <summary>
    /// Retires in-flight entries the server has reported as applied on <paramref name="connection"/>.
    /// </summary>
    /// <remarks>
    /// The connection identity is checked inside <see cref="_inFlightLock"/>, not before it.
    /// <see cref="ConnectCoreAsync"/> waits only a bounded time for the previous receive loop to
    /// exit, so a loop suspended between reading the connection and taking the lock could otherwise
    /// pass the check against the old connection, then, once resumed, remove an entry the new
    /// connection already recorded. Checking under the lock is sufficient rather than merely
    /// narrower: <see cref="RecordInFlight"/> and <see cref="ReParkInFlightChanges"/> both run under
    /// <c>_connectionLock</c>, which <see cref="ConnectCoreAsync"/> holds continuously from the map
    /// clear through the new socket's assignment, so while <see cref="_inFlightLock"/> is held, a
    /// connection field matching <paramref name="connection"/> implies every remaining entry belongs
    /// to it. Socket instances are never reused, so there is no ABA problem.
    /// </remarks>
    internal void RetireInFlightThrough(long appliedThrough, System.Net.WebSockets.WebSocket connection)
    {
        if (appliedThrough <= 0)
        {
            return;
        }

        BeforeRetireInFlightLock?.Invoke();

        lock (_inFlightLock)
        {
            if (!ReferenceEquals(_webSocket, connection) || _inFlight.Count == 0)
            {
                return;
            }

            foreach (var (property, entry) in _inFlight)
            {
                if (entry.Ordinal <= appliedThrough)
                {
                    _inFlight.Remove(property);
                }
            }
        }
    }

    /// <inheritdoc />
    public override Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
    {
        if (_initialState is null)
        {
            return Task.FromResult<Action?>(null);
        }

        return Task.FromResult<Action?>(() =>
        {
            var factory = _configuration.SubjectFactory ?? DefaultSubjectFactory.Instance;
            _subject.ApplySubjectUpdate(_initialState, factory, ChangeOrigin.FromSource(this));

            // Claim ownership of all properties matching the path provider
            ClaimPropertyOwnership();

            _initialState = null;
        });
    }

    private void ClaimPropertyOwnership()
    {
        var pathProvider = _configuration.PathProvider;

        var registeredSubject = _subject.TryGetRegisteredSubject();
        if (registeredSubject is null)
        {
            _logger.LogWarning("Subject is not registered. Cannot claim property ownership.");
            return;
        }

        var properties = registeredSubject
            .GetAllProperties()
            .Where(p => !p.CanContainSubjects && (pathProvider is null || pathProvider.IsPropertyIncluded(p)))
            .ToList();

        var claimedCount = 0;
        foreach (var property in properties)
        {
            if (_ownership.ClaimSource(property.Reference))
            {
                claimedCount++;
            }
            else
            {
                _logger.LogWarning(
                    "Property {Subject}.{Property} already owned by another source.",
                    property.Subject.GetType().Name, property.Name);
            }
        }

        _logger.LogInformation("Claimed ownership of {Count} properties for WebSocket sync.", claimedCount);
    }

    /// <inheritdoc />
    public override async ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        _logger.LogDebug("WriteChangesAsync called with {Count} changes", changes.Length);

        if (Volatile.Read(ref _disposed) == 1)
        {
            return WriteResult.Failure(changes, new ObjectDisposedException(nameof(WebSocketSubjectClientSource)));
        }

        // Captured before the wait: a batch queued on the lock across a reconnect would otherwise be
        // sent over the new connection carrying pre-outage values that nothing has reconciled. The
        // resume epoch is captured alongside the socket so the guard below can tell that case apart
        // from a fresh commit that also waits across the same reconnect.
        //
        // The pair has to be a consistent snapshot, not two independent reads: reading the socket then
        // the epoch, or the epoch then the socket, both leave a window in which a reconnect that lands
        // inside it produces a combination, old socket with new epoch or new socket with old epoch, that
        // the guard below cannot tell apart from a batch that genuinely predates the reconnect. Taken
        // seqlock style instead: read the epoch, read the socket, re-read the epoch, and retry the pair
        // if it moved. This is sound only because on every path that establishes a connection the epoch
        // bump always precedes the socket assignment, the same precondition CurrentResumeEpoch's own
        // remarks document, so an epoch that reads the same both times means no replacement began
        // between the two reads, and the socket read in between it genuinely belongs to that epoch.
        ClientWebSocket? socketAtEntry;
        int resumeEpochAtEntry;
        while (true)
        {
            resumeEpochAtEntry = CurrentResumeEpoch;
            BeforeSocketSnapshotRead?.Invoke();
            socketAtEntry = _webSocket;
            if (CurrentResumeEpoch == resumeEpochAtEntry)
            {
                break;
            }
        }

        AfterConnectionSnapshotCaptured?.Invoke(socketAtEntry, resumeEpochAtEntry);

        BeforeConnectionLockWait?.Invoke();

        try
        {
            await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return WriteResult.Failure(changes, new ObjectDisposedException(nameof(WebSocketSubjectClientSource)));
        }

        try
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return WriteResult.Failure(changes, new ObjectDisposedException(nameof(WebSocketSubjectClientSource)));
            }

            // Rejects a batch that predates the connection replacement. The socket differing is not
            // enough on its own: a fresh commit issued during the reconnect window also captures the
            // pre-reconnect socket while it waits on the lock above, and that write is not stale. The
            // resume epoch tells the two apart, since it only advances across an actual BeginResume.
            // Rejecting here is what parks a stale batch on the retry-queue path, where the reconcile
            // judges it; a transactional commit reaches this method directly with no such path, so a
            // rejection surfaces there as a commit failure instead, which is why the guard is narrowed
            // to batches that genuinely predate the reconnect rather than firing on every replacement.
            if (!ReferenceEquals(_webSocket, socketAtEntry) && CurrentResumeEpoch != resumeEpochAtEntry)
            {
                AfterConnectionReplacedGuardFired?.Invoke();
                return WriteResult.Failure(changes, new InvalidOperationException(
                    "The connection was replaced while this write waited for the connection lock."));
            }

            // Read again rather than reusing socketAtEntry: the epoch half of the guard above can let
            // this point through with the socket already replaced, in which case the send has to go
            // out on the connection that replaced it, not on the one captured before the wait.
            var webSocket = _webSocket;
            if (webSocket?.State != WebSocketState.Open)
            {
                return WriteResult.Failure(changes, new InvalidOperationException("WebSocket is not connected"));
            }

            var update = SubjectUpdate.CreatePartialUpdateFromChanges(_subject, changes.Span, _processors);
            _sendBuffer.Clear();
            _serializer.SerializeMessageTo(_sendBuffer, MessageType.Update, update);
            _logger.LogDebug("Sending {ByteCount} bytes ({SubjectCount} subjects) to server",
                _sendBuffer.WrittenCount, update.Subjects.Count);

            if (_acknowledgesAppliedUpdates)
            {
                // Reserved before the send, not assigned after it: see ReserveSendOrdinal's remarks for why.
                var sendOrdinal = ReserveSendOrdinal();
                BeforeReservedSendAttempt?.Invoke();
                try
                {
                    await webSocket.SendAsync(_sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // A failed send after the ordinal was reserved would otherwise leave a permanent
                    // gap: nothing above it can ever retire, so it is re-parked at every reconnect and
                    // re-asserts a stale value forever. Abort rather than returning a failure on a
                    // socket that might still be usable, so the reconnect this forces resets the ordinal
                    // and the gap cannot persist. This abort is the fix for that gap, not a redundant
                    // belt-and-braces call; do not remove it on the assumption the retry path alone
                    // covers a failed send here.
                    webSocket.Abort();
                    throw;
                }

                RecordInFlight(sendOrdinal, changes.Span);
            }
            else
            {
                // No acknowledgement on this connection: nothing would ever retire an entry recorded
                // here, and a re-parked entry that is re-sent flows back through this same method, so
                // recording anyway would grow the set across every reconnect for the life of the source.
                // Send without reserving an ordinal or recording, so the set stays empty and the re-park
                // on the next connect finds nothing to carry over.
                await webSocket.SendAsync(_sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }

            MaybeShrinkSendBuffer();
            _logger.LogDebug("Sent update successfully");
            return WriteResult.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send update to server");
            return WriteResult.Failure(changes, ex);
        }
        finally
        {
            try
            {
                _connectionLock.Release();
            }
            catch (ObjectDisposedException)
            {
                // Lock was disposed during operation
            }
        }
    }

    /// <summary>
    /// Reserves the ordinal for the send that is about to happen, before it happens.
    /// </summary>
    /// <remarks>
    /// Reserving ahead of the send rather than assigning after it means a send that reaches the peer
    /// while, for whatever reason, the matching <see cref="RecordInFlight"/> call is skipped can only
    /// leave the client's ordinal ahead of the server's count, never behind it. Behind would retire an
    /// in-flight entry the server never actually received, which is the loss this field exists to
    /// prevent. Ahead is not the safe alternative it first looks like: a send that fails after this
    /// reserves an ordinal leaves a gap that nothing above it can ever retire, so those entries are
    /// re-parked at every reconnect and re-assert stale values forever, which is a permanent loss of a
    /// different shape rather than a safe one. What actually closes that gap is the caller aborting the
    /// connection when a reserved send fails, in <see cref="WriteChangesAsync"/>, so the reconnect that
    /// follows resets the ordinal to zero.
    /// </remarks>
    private long ReserveSendOrdinal()
    {
        lock (_inFlightLock)
        {
            return ++_sendOrdinal;
        }
    }

    /// <summary>
    /// Records changes that reached the socket, keyed per property so a later send for the same
    /// property replaces the earlier one.
    /// </summary>
    /// <remarks>
    /// Called under the connection lock, so the ordinal matches the order the server receives the
    /// messages in. The count the server reports back is what retires these.
    /// </remarks>
    private void RecordInFlight(long ordinal, ReadOnlySpan<SubjectPropertyChange> changes)
    {
        lock (_inFlightLock)
        {
            foreach (var change in changes)
            {
                _inFlight[change.Property] = (ordinal, change);
            }
        }
    }

    private async Task ReceiveLoopAsync(
        CancellationToken cancellationToken,
        TaskCompletionSource completionSource,
        ConnectorCommitLease commitLease,
        ClientSequenceTracker sequenceTracker)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);

        // Reusable CTS to reduce allocations (reset instead of recreate when possible)
        var timeoutCts = new CancellationTokenSource();
        CancellationTokenSource? linkedCts = null;
        var consecutiveErrors = 0;

        // Scoped to this call, so it resets naturally on every reconnect rather than needing an explicit
        // reset: this loop runs once per connection.
        var warnedMissingAppliedThroughOnThisConnection = false;

        try
        {
            // Capture once: this receive loop is tied to a single connection.
            var webSocket = _webSocket;
            while (!cancellationToken.IsCancellationRequested && webSocket?.State == WebSocketState.Open)
            {
                try
                {
                    var messageStream = MemoryStreamManager.GetStream();
                    await using (messageStream.ConfigureAwait(false))
                    {
                        // Reset or recreate the timeout CTS for each message
                        if (!timeoutCts.TryReset())
                        {
                            timeoutCts.Dispose();
                            timeoutCts = new CancellationTokenSource();
                        }

                        timeoutCts.CancelAfter(_configuration.ReceiveTimeout);

                        // Create linked token for this message
                        linkedCts?.Dispose();
                        linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                        // Use shared utility for fragmented message receive
                        var readResult = await WebSocketMessageReader.ReadMessageIntoStreamAsync(
                            webSocket, buffer, messageStream, _configuration.MaxMessageSize, linkedCts.Token).ConfigureAwait(false);

                        if (readResult.IsCloseMessage)
                        {
                            _logger.LogInformation("Server closed connection");
                            return;
                        }

                        if (readResult.ExceededMaxSize)
                        {
                            _logger.LogWarning("Message exceeds maximum size of {MaxSize} bytes", _configuration.MaxMessageSize);
                            throw new InvalidOperationException($"Message exceeds maximum size of {_configuration.MaxMessageSize} bytes");
                        }

                        var messageBytes = new ReadOnlySpan<byte>(messageStream.GetBuffer(), 0, (int)messageStream.Length);
                        var (messageType, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(messageBytes);
                        var payloadBytes = messageBytes.Slice(payloadStart, payloadLength);

                        switch (messageType)
                        {
                            case MessageType.Update:
                                var update = _serializer.Deserialize<UpdatePayload>(payloadBytes);
                                if (update.Sequence is not null && !sequenceTracker.IsUpdateValid(update.Sequence.Value))
                                {
                                    _logger.LogWarning(
                                        "Sequence gap detected: expected {Expected}, received {Received}. Triggering reconnection.",
                                        sequenceTracker.ExpectedNextSequence, update.Sequence);
                                    return; // Exit receive loop -> triggers reconnection
                                }
                                HandleUpdate(update, commitLease);
                                break;

                            case MessageType.Heartbeat:
                                var heartbeat = _serializer.Deserialize<HeartbeatPayload>(payloadBytes);
                                if (!sequenceTracker.IsHeartbeatInSync(heartbeat.Sequence))
                                {
                                    _logger.LogWarning(
                                        "Heartbeat sequence gap: server at {ServerSequence}, client expects {Expected}. Triggering reconnection.",
                                        heartbeat.Sequence, sequenceTracker.ExpectedNextSequence);
                                    return; // Exit receive loop -> triggers reconnection
                                }

                                if (heartbeat.AppliedThrough is { } appliedThrough)
                                {
                                    // The loop's own socket, not the field: a suspended zombie loop must
                                    // not retire against the connection that replaced it.
                                    RetireInFlightThrough(appliedThrough, webSocket);
                                }
                                else if (_acknowledgesAppliedUpdates && !warnedMissingAppliedThroughOnThisConnection)
                                {
                                    // The server promised acknowledgement at connect and has now sent a
                                    // heartbeat without the value it promised. No peer in this repository
                                    // can produce that state, so this is a foreign or misbehaving
                                    // implementation. Warn once and leave the mode as is: flipping it
                                    // mid-connection would race against entries already recorded under
                                    // the acknowledging assumption.
                                    warnedMissingAppliedThroughOnThisConnection = true;
                                    _logger.LogWarning(
                                        "Server advertised applied-update acknowledgement at connect but sent a heartbeat without an applied-through value. Ignoring; the in-flight set for this connection is left as is.");
                                }
                                break;

                            case MessageType.Error:
                                var error = _serializer.Deserialize<ErrorPayload>(payloadBytes);
                                _logger.LogWarning("Received error from server: {Code} - {Message}", error.Code, error.Message);
                                break;
                        }

                        consecutiveErrors = 0;
                    }
                }
                catch (WebSocketException ex)
                {
                    _logger.LogWarning(ex, "WebSocket error in receive loop");
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Normal shutdown
                    break;
                }
                catch (OperationCanceledException)
                {
                    // Receive timeout - connection considered lost
                    _logger.LogWarning("Receive timeout exceeded ({Timeout}), connection considered lost", _configuration.ReceiveTimeout);
                    break;
                }
                catch (Exception ex)
                {
                    consecutiveErrors++;
                    _logger.LogError(ex, "Error processing received message (consecutive errors: {Count})", consecutiveErrors);

                    const int maxConsecutiveReceiveErrors = 5;
                    if (consecutiveErrors >= maxConsecutiveReceiveErrors)
                    {
                        _logger.LogError("Too many consecutive errors ({Count}), exiting receive loop", consecutiveErrors);
                        break;
                    }
                }
            }
        }
        finally
        {
            try
            {
                MarkReceiveLoopNotOperational(completionSource);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                timeoutCts.Dispose();
                linkedCts?.Dispose();

                // Signal that receive loop has completed (for reconnection handling)
                completionSource.TrySetResult();
            }
        }
    }

    private void PublishReceiveLoopAndMarkOperational(TaskCompletionSource completionSource)
    {
        BeforeReceiveLoopPublication?.Invoke();

        lock (_receiveLoopLivenessLock)
        {
            _receiveLoopLivenessOwner = completionSource;
            Metrics.MarkOperational();
        }
    }

    private void MarkReceiveLoopNotOperational(TaskCompletionSource completionSource)
    {
        lock (_receiveLoopLivenessLock)
        {
            if (ReferenceEquals(_receiveLoopLivenessOwner, completionSource))
            {
                BeforeReceiveLoopLivenessTransition?.Invoke();
                Metrics.MarkNotOperational();
            }
        }
    }

    private void MarkNotOperationalAfterFailedConnection()
    {
        lock (_receiveLoopLivenessLock)
        {
            Metrics.MarkNotOperational();
        }
    }

    private void HandleUpdate(SubjectUpdate update, ConnectorCommitLease commitLease)
    {
        BeforeUpdateCommitAdmission?.Invoke();

        var propertyWriter = _propertyWriter;
        if (propertyWriter is null) return;

        propertyWriter.Write(
            (update, subject: _subject, factory: _configuration.SubjectFactory ?? DefaultSubjectFactory.Instance, source: this, commitLease, logger: _logger),
            static state =>
            {
                if (!state.commitLease.TryAcquireCommit())
                {
                    return;
                }

                try
                {
                    state.subject.ApplySubjectUpdate(
                        state.update,
                        state.factory,
                        ChangeOrigin.FromSource(state.source),
                        logger: state.logger);
                }
                finally
                {
                    state.commitLease.ReleaseCommit();
                }
            });
    }

    /// <inheritdoc />
    async Task IFaultInjectable.InjectFaultAsync(FaultType faultType, CancellationToken cancellationToken)
    {
        switch (faultType)
        {
            case FaultType.Kill:
                await ForceKillCurrentAttemptAsync().ConfigureAwait(false);
                break;

            case FaultType.Disconnect:
                _webSocket?.Abort();
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(faultType), faultType, null);
        }
    }

    /// <summary>
    /// Monitor connection and handle reconnection with exponential backoff.
    /// Spawned by <see cref="StartListeningAsync"/> via <see cref="BackgroundTaskLifetime.Start"/>.
    /// Cancellation of <paramref name="stoppingToken"/> (via lifetime disposal or host shutdown)
    /// breaks the outer loop.
    /// </summary>
    private async Task RunMonitorLoopAsync(CancellationToken stoppingToken)
    {
        // Monitor connection and handle reconnection with exponential backoff
        var reconnectDelay = _configuration.ReconnectDelay;
        var maxDelay = _configuration.MaxReconnectDelay;
        var forceReconnect = false;

        // Carries the epoch across loop iterations the same way forceReconnect does: BeginResume runs
        // in one iteration (drop detection or a force-kill catch) and the matching CompleteResumeAsync
        // or AbortResume runs in the ReconnectAndResumeAsync call that follows, possibly on a later one.
        var resumeEpoch = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var stopMonitoring = false;

            await RunAttemptAsync(stoppingToken, async attempt =>
            {
                var linkedToken = attempt.Token;

                try
                {
                    if (forceReconnect)
                    {
                        forceReconnect = false;
                        reconnectDelay = await ReconnectAndResumeAsync(
                            "WebSocket reconnected after force-kill", resumeEpoch, reconnectDelay, maxDelay, linkedToken).ConfigureAwait(false);
                        return;
                    }

                    // Wait for receive loop to complete (connection dropped)
                    var receiveLoopTask = _receiveLoopCompleted?.Task;
                    if (receiveLoopTask is not null)
                    {
                        await receiveLoopTask.WaitAsync(linkedToken).ConfigureAwait(false);
                    }

                    // Connection dropped - check if we should reconnect
                    if (stoppingToken.IsCancellationRequested || Volatile.Read(ref _disposed) == 1)
                    {
                        stopMonitoring = true;
                        return;
                    }

                    _logger.LogWarning("WebSocket connection lost. Attempting reconnection in {Delay}...", reconnectDelay);

                    resumeEpoch = BeginResume();
                    _propertyWriter?.StartBuffering();

                    // Circuit breaker: pause reconnection if too many consecutive failures
                    if (_circuitBreaker is not null && !_circuitBreaker.ShouldAttempt())
                    {
                        var cooldownRemaining = _circuitBreaker.GetCooldownRemaining();
                        _logger.LogWarning(
                            "Circuit breaker open after {TripCount} trips. Pausing reconnection attempts for {Cooldown}s.",
                            _circuitBreaker.TripCount,
                            (int)cooldownRemaining.TotalSeconds);

                        await Task.Delay(cooldownRemaining, linkedToken).ConfigureAwait(false);

                        // Reset backoff after cooldown so the first retry is fast
                        reconnectDelay = _configuration.ReconnectDelay;
                    }

                    await Task.Delay(reconnectDelay, linkedToken).ConfigureAwait(false);

                    reconnectDelay = await ReconnectAndResumeAsync(
                        "WebSocket reconnected successfully", resumeEpoch, reconnectDelay, maxDelay, linkedToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                }
                catch (OperationCanceledException) when (attempt.WasForceKilled)
                {
                    _logger.LogWarning("WebSocket client force-killed. Restarting...");
                    _webSocket?.Abort();
                    resumeEpoch = BeginResume();
                    _propertyWriter?.StartBuffering();
                    forceReconnect = true;
                }
                catch (Exception ex)
                {
                    // Nothing outside this loop reports its failures, but a stop tears the socket down with
                    // a WebSocketException or ObjectDisposedException rather than a cancellation, so only
                    // the stopping token tells a shutdown apart from a genuine fault.
                    if (!stoppingToken.IsCancellationRequested)
                    {
                        Metrics.ReportError(ex);
                    }

                    _logger.LogError(ex, "Error in WebSocket connection monitoring");
                }
            }).ConfigureAwait(false);

            if (stopMonitoring)
            {
                break;
            }
        }
    }

    private async Task<TimeSpan> ReconnectAndResumeAsync(
        string successMessage, int resumeEpoch, TimeSpan reconnectDelay, TimeSpan maxDelay, CancellationToken cancellationToken)
    {
        try
        {
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            _circuitBreaker?.RecordSuccess();
            _logger.LogInformation(successMessage);

            // The socket is writable and the receive loop is already running at this point, but the
            // model still holds the pre-reconnect view: this is the window D4 is about.
            BeforeReconnectInitialStateLoad?.Invoke();

            if (_propertyWriter is not null)
            {
                await _propertyWriter.LoadInitialStateAndResumeAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            // After the load, never before: the parked writes are judged against the state the server
            // just sent rather than replayed over it, and a write a later local commit supersedes is
            // dropped instead of being sent after the newer one.
            await CompleteResumeAsync(resumeEpoch, cancellationToken).ConfigureAwait(false);

            return _configuration.ReconnectDelay;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // ConnectAsync or the load can have already succeeded, with the socket open and the receive
            // loop running, before this cancellation was observed. Cleared here too, not only in the
            // catch below, so a cancellation that lands on this arm cannot leave the gate held for the
            // life of that connection the way an unguarded rethrow would.
            AbortResume(resumeEpoch);
            throw;
        }
        catch (Exception ex)
        {
            // ConnectAsync can have already succeeded, with the socket open and the receive loop running,
            // when the load that follows it throws. Nothing else clears this gate for that connection: the
            // attempt loop does not iterate on a transport reconnect, so leaving it set would park every
            // write for the life of the connection. The cost is that whatever this epoch had parked is
            // never reconciled against a loaded state: an unreconciled flush on a later successful write
            // beats a gate stuck for good. Cleared ahead of the rethrow below, which cancellation
            // surfacing as a transport exception would otherwise skip past.
            AbortResume(resumeEpoch);

            // Cancellation may surface as a transport exception rather than an OperationCanceledException.
            cancellationToken.ThrowIfCancellationRequested();
            Metrics.ReportError(ex);

            _logger.LogError(ex, "Failed to reconnect to WebSocket server");

            if (_circuitBreaker is not null && _circuitBreaker.RecordFailure())
            {
                _logger.LogWarning(
                    "Circuit breaker tripped after {Threshold} consecutive failures. " +
                    "Pausing reconnection attempts for {Cooldown}s.",
                    _configuration.CircuitBreakerFailureThreshold,
                    (int)_configuration.CircuitBreakerCooldown.TotalSeconds);
            }

            // Exponential backoff with equal jitter (0.5 to 1.0) to decorrelate reconnection attempts
            var jitter = Random.Shared.NextDouble() * 0.5 + 0.5;
            return TimeSpan.FromMilliseconds(
                Math.Min(reconnectDelay.TotalMilliseconds * 2 * jitter, maxDelay.TotalMilliseconds));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        // Stop the base ExecuteAsync first: this cancels the stoppingToken and asks the listen
        // lifetime (which owns the monitor task) to wind down. The stop is capped, so it can return
        // while that teardown is still in flight; the retirement drain below is what actually keeps
        // a straggling commit from landing after disposal.
        try
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await StopAsync(stopCts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort stop
        }

        // Belt-and-braces: retire the lease and cancel and dispose any straggler receive CTS / socket.
        await StopReceiveLoopAndDisposeSocketAsync().ConfigureAwait(false);

        // Clean up resources
        _ownership.Dispose();
        _connectionLock.Dispose();

        Dispose();
    }

    private void MaybeShrinkSendBuffer()
    {
        if (_sendBuffer is { Capacity: > SendBufferShrinkThreshold, WrittenCount: < SendBufferShrinkThreshold / 4 })
        {
            _sendBuffer = new ArrayBufferWriter<byte>(4096);
        }
    }

    // Test seams for interleavings that have no externally observable synchronization point: the
    // instant between a received update and its lease admission, the two sides of the liveness
    // lock, the instant the commit drain releases the teardown, and the instant a reconnect has a
    // live connection but has not yet loaded and reconciled. Always null in production; the tests
    // block inside them or sample ordering from them.
    internal Action? BeforeUpdateCommitAdmission { get; set; }

    internal Action? BeforeReceiveLoopLivenessTransition { get; set; }

    internal Action? BeforeReceiveLoopPublication { get; set; }

    internal Action? AfterReceiveLoopCommitDrain { get; set; }

    internal Action? BeforeReconnectInitialStateLoad { get; set; }

    /// <summary>
    /// Fires in <see cref="WriteChangesAsync"/>'s connection snapshot loop, after the resume epoch is
    /// read and before the socket is read, so a test can hold a write there while a reconnect races the
    /// two reads underneath it. Fires on every iteration of the loop, including retries.
    /// </summary>
    internal Action? BeforeSocketSnapshotRead { get; set; }

    /// <summary>
    /// Fires in <see cref="WriteChangesAsync"/> once the connection snapshot loop has produced a pair
    /// it accepts, carrying the socket and resume epoch it captured, so a test can assert the pair is
    /// self-consistent with the source's current state rather than a torn combination of an old and a
    /// new read.
    /// </summary>
    internal Action<ClientWebSocket?, int>? AfterConnectionSnapshotCaptured { get; set; }

    /// <summary>
    /// Fires in <see cref="WriteChangesAsync"/> right after an ordinal is reserved for the send that is
    /// about to happen, before the send is attempted, so a test can force that specific send to fail
    /// without depending on whatever a real transport happens to do when a send fails.
    /// </summary>
    internal Action? BeforeReservedSendAttempt { get; set; }

    /// <summary>
    /// Fires in <see cref="WriteChangesAsync"/> right after the current socket is captured and before
    /// waiting on <see cref="_connectionLock"/>, so a test can hold a write there while a reconnect
    /// replaces the connection underneath it.
    /// </summary>
    internal Action? BeforeConnectionLockWait { get; set; }

    /// <summary>
    /// Fires right after <see cref="WriteChangesAsync"/> has detected that the connection was replaced
    /// while it waited for <see cref="_connectionLock"/>, before the resulting failure is parked. The
    /// park that follows is drained by the connected-phase retry flush almost immediately, so a test
    /// needs this rather than polling the retry queue's depth for a window that may never be sampled.
    /// </summary>
    internal Action? AfterConnectionReplacedGuardFired { get; set; }

    /// <summary>
    /// Fires in <see cref="RetireInFlightThrough"/> right before it takes <see cref="_inFlightLock"/>,
    /// so a test can hold a retire call there while a reconnect replaces the connection underneath it.
    /// </summary>
    internal Action? BeforeRetireInFlightLock { get; set; }

    /// <summary>Number of writes that reached the socket on the current connection but are not yet retired.</summary>
    internal int InFlightCount
    {
        get
        {
            lock (_inFlightLock)
            {
                return _inFlight.Count;
            }
        }
    }
}
