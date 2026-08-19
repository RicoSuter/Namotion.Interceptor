using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

public class WriteRetryQueueTests
{
    [Fact]
    public async Task WhenEnqueueAndFlush_ThenChangesAreWritten()
    {
        // Arrange
        var queue = new WriteRetryQueue(100, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var sourceMock = new Mock<ISubjectSource>();

        SubjectPropertyChange[]? writtenChanges = null;
        sourceMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                writtenChanges = changes.ToArray();
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        // Act
        queue.Enqueue(CreateChanges(3));
        var result = await queue.FlushAsync(sourceMock.Object, CancellationToken.None);

        // Assert
        Assert.True(result);
        Assert.NotNull(writtenChanges);
        Assert.Equal(3, writtenChanges.Length);
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public async Task WhenQueueIsEmpty_ThenFlushReturnsTrue()
    {
        // Arrange
        var queue = new WriteRetryQueue(100, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var sourceMock = new Mock<ISubjectSource>();

        // Act
        var result = await queue.FlushAsync(sourceMock.Object, CancellationToken.None);

        // Assert
        Assert.True(result);
        sourceMock.Verify(
            c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task WhenQueueIsFull_ThenOldestAreDropped()
    {
        // Arrange
        var queue = new WriteRetryQueue(5, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var sourceMock = new Mock<ISubjectSource>();

        SubjectPropertyChange[]? writtenChanges = null;
        sourceMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                writtenChanges = changes.ToArray();
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        // Act
        queue.Enqueue(CreateChanges(3, startId: 0));  // Items 0, 1, 2
        queue.Enqueue(CreateChanges(4, startId: 10)); // Items 10, 11, 12, 13 -> should drop 0, 1

        // Assert
        Assert.Equal(5, queue.PendingWriteCount);

        // Flush and verify the actual items (2, 10, 11, 12, 13)
        await queue.FlushAsync(sourceMock.Object, CancellationToken.None);

        Assert.NotNull(writtenChanges);
        Assert.Equal(5, writtenChanges.Length);
        Assert.Equal(2, writtenChanges[0].GetOldValue<int>());   // Item 2
        Assert.Equal(10, writtenChanges[1].GetOldValue<int>());  // Item 10
        Assert.Equal(11, writtenChanges[2].GetOldValue<int>());  // Item 11
        Assert.Equal(12, writtenChanges[3].GetOldValue<int>());  // Item 12
        Assert.Equal(13, writtenChanges[4].GetOldValue<int>());  // Item 13
    }

    [Fact]
    public async Task WhenFlushFails_ThenChangesAreRequeued()
    {
        // Arrange
        var queue = new WriteRetryQueue(100, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var sourceMock = new Mock<ISubjectSource>();

        sourceMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
                new ValueTask<WriteResult>(WriteResult.Failure(changes, new Exception("Connection failed"))));

        // Act
        queue.Enqueue(CreateChanges(3));
        var result = await queue.FlushAsync(sourceMock.Object, CancellationToken.None);

        // Assert
        Assert.False(result);
        Assert.Equal(3, queue.PendingWriteCount); // Re-queued
    }

    [Fact]
    public async Task WhenFlushFailsWithoutEnumeratedFailedChanges_ThenWholeBatchIsRequeued()
    {
        // Arrange: the source fails wholesale but does not enumerate the failed changes.
        var queue = new WriteRetryQueue(100, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var sourceMock = new Mock<ISubjectSource>();

        sourceMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
                new ValueTask<WriteResult>(WriteResult.Failure(
                    ReadOnlyMemory<SubjectPropertyChange>.Empty, new Exception("Connection failed"))));

        // Act
        queue.Enqueue(CreateChanges(3));
        var result = await queue.FlushAsync(sourceMock.Object, CancellationToken.None);

        // Assert
        Assert.False(result);
        Assert.Equal(3, queue.PendingWriteCount); // the whole attempted batch is re-queued, not dropped
    }

    [Fact]
    public async Task WhenFlushFailsAtCapacity_ThenRequeueDoesNotDropItems()
    {
        // Arrange
        var queue = new WriteRetryQueue(5, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var sourceMock = new Mock<ISubjectSource>();

        sourceMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
                new ValueTask<WriteResult>(WriteResult.Failure(changes, new Exception("Connection failed"))));

        // Act
        queue.Enqueue(CreateChanges(5)); // Fill to capacity
        var result = await queue.FlushAsync(sourceMock.Object, CancellationToken.None);

        // Assert
        Assert.False(result);
        Assert.Equal(5, queue.PendingWriteCount); // All items re-queued, none dropped
    }

    [Fact]
    public async Task WhenFailedInflightBatchIsRequeuedAfterNewWritesFillCapacity_ThenOldestFailedWritesAreDropped()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(SourceMetrics.OutboundRetries));
        var diagnostics = new QueueDiagnostics(metrics);
        var queue = new WriteRetryQueue(2, NullLogger.Instance, metrics);
        var sourceMock = new Mock<ISubjectSource>();
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        sourceMock
            .Setup(source => source.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                writeStarted.SetResult();
                await completeWrite.Task;
                return WriteResult.Failure(changes, new InvalidOperationException("Connection failed"));
            });

        queue.Enqueue(CreateChanges(2, startId: 0)); // A/B

        // Act
        var flush = queue.FlushAsync(sourceMock.Object, CancellationToken.None);
        await writeStarted.Task;
        queue.Enqueue(CreateChanges(2, startId: 2)); // C/D
        Assert.Equal(2, queue.PendingWriteCount);
        completeWrite.SetResult();
        var result = await flush;
        var retained = queue.DrainForLocalReapply();

        // Assert
        Assert.False(result);
        Assert.Equal(2, retained.Length);
        Assert.Equal(2, retained[0].GetOldValue<int>());
        Assert.Equal(3, retained[1].GetOldValue<int>());
        Assert.Equal(2, diagnostics.TotalDropped);
    }

    [Fact]
    public void WhenMaxQueueSizeIsZero_ThenWritesAreDropped()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(SourceMetrics.OutboundRetries));
        var queue = new WriteRetryQueue(0, NullLogger.Instance, metrics);
        var diagnostics = new QueueDiagnostics(metrics);

        // Act
        queue.Enqueue(CreateChanges(5));

        // Assert
        Assert.True(queue.IsEmpty);
        Assert.Equal(0, queue.PendingWriteCount);
        Assert.Equal(5, diagnostics.TotalDropped);
    }

    [Fact]
    public async Task WhenManyItems_ThenFlushProcessesInBatches()
    {
        // Arrange
        var queue = new WriteRetryQueue(2000, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var sourceMock = new Mock<ISubjectSource>();

        var totalWritten = 0;
        sourceMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                totalWritten += changes.Length;
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        // Act - enqueue more than MaxBatchSize (1024)
        queue.Enqueue(CreateChanges(1500));
        var result = await queue.FlushAsync(sourceMock.Object, CancellationToken.None);

        // Assert
        Assert.True(result);
        Assert.Equal(1500, totalWritten);
        Assert.True(queue.IsEmpty);

        // Should have been called at least twice (1024 + 476)
        sourceMock.Verify(
            c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(2));
    }

    [Fact]
    public async Task WhenCancelled_ThenFlushReturnsFalse()
    {
        // Arrange
        var queue = new WriteRetryQueue(100, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var sourceMock = new Mock<ISubjectSource>();
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        queue.Enqueue(CreateChanges(3));

        // Act
        var result = await queue.FlushAsync(sourceMock.Object, cts.Token);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task WhenMultipleFlushes_ThenOnlyOneRunsAtATime()
    {
        // Arrange
        var queue = new WriteRetryQueue(100, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var sourceMock = new Mock<ISubjectSource>();

        var callCount = 0;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        sourceMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken ct) =>
            {
                Interlocked.Increment(ref callCount);
                await tcs.Task; // Block until released
                return WriteResult.Success;
            });

        queue.Enqueue(CreateChanges(3));

        // Act - start two flushes
        var flush1 = queue.FlushAsync(sourceMock.Object, CancellationToken.None);
        var flush2 = queue.FlushAsync(sourceMock.Object, CancellationToken.None);

        await AsyncTestHelpers.WaitUntilAsync(() => callCount >= 1);

        // Assert - only one should be running
        Assert.Equal(1, callCount);

        tcs.SetResult(); // Release the blocked flush
        await flush1;
        await flush2;
    }

    [Fact]
    public async Task WhenSourceBatchSizeSet_ThenWriteChangesInBatchesRespectsBatchSize()
    {
        // Arrange
        var queue = new WriteRetryQueue(100, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var sourceMock = new Mock<ISubjectSource>();

        var batchSizes = new List<int>();
        sourceMock.Setup(c => c.WriteBatchSize).Returns(2);
        sourceMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                batchSizes.Add(changes.Length);
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        // Act
        queue.Enqueue(CreateChanges(5));
        await queue.FlushAsync(sourceMock.Object, CancellationToken.None);

        // Assert - WriteChangesInBatchesAsync should split into batches of 2
        Assert.Equal(3, batchSizes.Count); // 2 + 2 + 1
        Assert.Equal(2, batchSizes[0]);
        Assert.Equal(2, batchSizes[1]);
        Assert.Equal(1, batchSizes[2]);
    }

    [Fact]
    public async Task WhenConcurrentEnqueues_ThenAllItemsAreQueued()
    {
        // Arrange
        var queue = new WriteRetryQueue(10000, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var tasks = new List<Task>();

        // Act - enqueue from multiple threads
        for (var i = 0; i < 10; i++)
        {
            var batch = i;
            tasks.Add(Task.Run(() => queue.Enqueue(CreateChanges(100, startId: batch * 100))));
        }

        await Task.WhenAll(tasks);

        // Assert
        Assert.Equal(1000, queue.PendingWriteCount);
    }

    [Fact]
    public async Task WhenFlushSucceeds_ThenQueueIsEmpty()
    {
        // Arrange
        var queue = new WriteRetryQueue(100, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var sourceMock = new Mock<ISubjectSource>();

        sourceMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
                new ValueTask<WriteResult>(WriteResult.Success));

        // Act
        queue.Enqueue(CreateChanges(10));
        await queue.FlushAsync(sourceMock.Object, CancellationToken.None);

        // Assert - queue should be empty after successful flush
        Assert.True(queue.IsEmpty);
        Assert.Equal(0, queue.PendingWriteCount);
    }

    [Fact]
    public async Task WhenExactlyMaxBatchSizeItems_ThenAllItemsAreFlushed()
    {
        // Arrange
        var queue = new WriteRetryQueue(2000, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var sourceMock = new Mock<ISubjectSource>();

        var totalWritten = 0;
        sourceMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                totalWritten += changes.Length;
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        // Act - enqueue exactly MaxBatchSize (1024)
        queue.Enqueue(CreateChanges(1024));
        await queue.FlushAsync(sourceMock.Object, CancellationToken.None);

        // Assert
        Assert.Equal(1024, totalWritten);
        Assert.True(queue.IsEmpty);
    }
    
    [Fact]
    public void WhenDrainForLocalReapply_ThenReturnsAllItemsAndClearsQueue()
    {
        // Arrange
        var queue = new WriteRetryQueue(100, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        queue.Enqueue(CreateChanges(5));
        Assert.Equal(5, queue.PendingWriteCount);

        // Act
        var drained = queue.DrainForLocalReapply();

        // Assert
        Assert.Equal(5, drained.Length);
        Assert.True(queue.IsEmpty);
        Assert.Equal(0, queue.PendingWriteCount);
    }

    [Fact]
    public void WhenDrainForLocalReapplyOnEmptyQueue_ThenReturnsEmptyArray()
    {
        // Arrange
        var queue = new WriteRetryQueue(100, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));

        // Act
        var drained = queue.DrainForLocalReapply();

        // Assert
        Assert.Empty(drained);
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public async Task WhenFlushKeepsFailingForOneProperty_ThenQueueDoesNotGrowPerTick()
    {
        // Arrange
        var queue = new WriteRetryQueue(1000, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var sourceMock = new Mock<ISubjectSource>();

        sourceMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
                new ValueTask<WriteResult>(WriteResult.Failure(changes, new Exception("Connection failed"))));

        var property = CreateProperty("Value");

        // Act - ten ticks that all fail, each writing the same property again. Mirrors the pump, which
        // flushes first and enqueues the tick's own changes when that flush failed.
        for (var revision = 1; revision <= 10; revision++)
        {
            await queue.FlushAsync(sourceMock.Object, CancellationToken.None);
            queue.Enqueue(new[] { CreateChange(property, revision, revision) });
        }

        // Assert - the requeued survivor plus the last tick's own write, not one entry per tick
        Assert.Equal(2, queue.PendingWriteCount);
    }

    [Fact]
    public async Task WhenTwoChangesForOnePropertyAreQueued_ThenOneWriteCarriesTheHigherRevisionValue()
    {
        // Arrange
        var queue = new WriteRetryQueue(100, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var writes = new List<SubjectPropertyChange[]>();
        var sourceMock = CreateRecordingSource(writes);

        var property = CreateProperty("Value");

        // Act
        queue.Enqueue(new[]
        {
            CreateChange(property, 1, revision: 3),
            CreateChange(property, 2, revision: 7)
        });
        await queue.FlushAsync(sourceMock.Object, CancellationToken.None);

        // Assert - the pair collapses to one write spanning both commits, ranked by revision
        var write = Assert.Single(writes);
        var survivor = Assert.Single(write);
        Assert.Equal(7, survivor.Revision);
        Assert.Equal(0, survivor.GetOldValue<int>()); // old value of the older commit
        Assert.Equal(2, survivor.GetNewValue<int>());
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public async Task WhenPropertyIsWrittenTwice_ThenSurvivorKeepsNewestValueAtFirstPosition()
    {
        // Arrange
        var queue = new WriteRetryQueue(100, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var sourceMock = new Mock<ISubjectSource>();

        SubjectPropertyChange[]? writtenChanges = null;
        sourceMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                writtenChanges = changes.ToArray();
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        var collapsed = CreateProperty("Collapsed");
        var other = CreateProperty("Other");

        // Act
        queue.Enqueue(new[]
        {
            CreateChange(collapsed, 1, revision: 1),
            CreateChange(other, 10, revision: 2),
            CreateChange(collapsed, 2, revision: 3)
        });
        await queue.FlushAsync(sourceMock.Object, CancellationToken.None);

        // Assert
        Assert.NotNull(writtenChanges);
        Assert.Equal(2, writtenChanges.Length);

        Assert.Equal(collapsed, writtenChanges[0].Property); // survivor stays at the first occurrence
        Assert.Equal(0, writtenChanges[0].GetOldValue<int>()); // oldest commit's old value
        Assert.Equal(2, writtenChanges[0].GetNewValue<int>()); // newest commit's new value
        Assert.Equal(3, writtenChanges[0].Revision);

        Assert.Equal(other, writtenChanges[1].Property); // other properties keep their relative order
        Assert.Equal(10, writtenChanges[1].GetNewValue<int>());
    }

    [Fact]
    public void WhenFlushKeepsFailing_ThenCollapsingAllocatesNothingPerTick()
    {
        // Arrange - the source hands back a pre-built failure, so a tick allocates only what the flush
        // itself does. Measured against a control that flushes the same way with a single entry and
        // therefore nothing to collapse: a debug build allocates an async state machine per call either
        // way, which an absolute bound could not tell apart from a per-collapse collection.
        const int iterations = 200;
        var property = CreateProperty("Value");
        var source = new PrebuiltFailureSource(CreateChange(property, 0, revision: 1));

        // Act
        var withoutCollapse = MeasureFailingTicks(source, property, iterations, writePerTick: false);
        var withCollapse = MeasureFailingTicks(source, property, iterations, writePerTick: true);

        // Assert - a dictionary or list per collapse would cost hundreds of bytes per tick
        Assert.True(withCollapse - withoutCollapse <= 16,
            $"Collapsing allocated {withCollapse - withoutCollapse} bytes per failing tick " +
            $"({withCollapse} with it, {withoutCollapse} without).");
    }

    /// <summary>
    /// Independent measurement rounds taken per allocation reading, of which the lowest is reported.
    /// </summary>
    private const int MeasurementRounds = 5;

    /// <summary>
    /// Runs failing flush ticks and returns the bytes allocated per tick, taken as the lowest of
    /// several rounds. Stray allocation only ever inflates a round, so the cheapest round is the one
    /// that ran clean, and any leftover one-time cost lands in the first round and is discarded. An
    /// allocation per collapse is present in every round and so survives the minimum, which is what
    /// keeps the bound as sharp as a single sample.
    /// The queue holds the requeued survivor alone, or that plus the tick's own write when
    /// <paramref name="writePerTick"/> is set, which is what gives the collapse two entries for one
    /// property to merge.
    /// </summary>
    private static long MeasureFailingTicks(ISubjectSource source, PropertyReference property, int iterations, bool writePerTick)
    {
        var queue = new WriteRetryQueue(1000, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var tickBuffer = new SubjectPropertyChange[1];

        tickBuffer[0] = CreateChange(property, 1, revision: 1);
        queue.Enqueue(tickBuffer);

        // Warm up: the first ticks size the pending list, the flush scratch buffer and the collapse index.
        var revision = 2L;
        for (; revision <= 21; revision++)
        {
            RunFailingTick(queue, source, property, revision, tickBuffer, writePerTick);
        }

        var lowest = long.MaxValue;
        for (var round = 0; round < MeasurementRounds; round++)
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var tick = 0; tick < iterations; tick++, revision++)
            {
                RunFailingTick(queue, source, property, revision, tickBuffer, writePerTick);
            }

            lowest = Math.Min(lowest, (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / iterations);
        }

        Assert.Equal(writePerTick ? 2 : 1, queue.PendingWriteCount);
        return lowest;
    }

    private static void RunFailingTick(
        WriteRetryQueue queue, ISubjectSource source, PropertyReference property, long revision,
        SubjectPropertyChange[] tickBuffer, bool writePerTick)
    {
        var flush = queue.FlushAsync(source, CancellationToken.None);

        // Everything completes synchronously here, which is what keeps the allocations on this thread.
        Assert.True(flush.IsCompleted);
        Assert.False(flush.GetAwaiter().GetResult());

        if (writePerTick)
        {
            tickBuffer[0] = CreateChange(property, (int)revision, revision);
            queue.Enqueue(tickBuffer);
        }
    }

    private static PropertyReference CreateProperty(string name)
    {
        return new PropertyReference(new Mock<IInterceptorSubject>().Object, name);
    }

    /// <summary>
    /// A source that writes everything successfully and records each write it is handed. The mock's
    /// default batch size of zero keeps one write per flush round, which is the granularity the
    /// one-change-per-property guarantee is stated at.
    /// </summary>
    private static Mock<ISubjectSource> CreateRecordingSource(List<SubjectPropertyChange[]> writes)
    {
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                writes.Add(changes.ToArray());
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        return sourceMock;
    }

    private static SubjectPropertyChange CreateChange(PropertyReference property, int newValue, long revision)
    {
        return SubjectPropertyChange.Create(
            property,
            ChangeOrigin.Local,
            DateTimeOffset.UtcNow,
            null,
            newValue - 1,
            newValue,
            revision);
    }

    /// <summary>
    /// Fails every write with a result built once, so a failing tick allocates nothing of its own.
    /// </summary>
    private sealed class PrebuiltFailureSource : ISubjectSource, ISupportsConcurrentWrites
    {
        private readonly WriteResult _result;

        public PrebuiltFailureSource(SubjectPropertyChange failedChange)
        {
            _result = WriteResult.Failure(new[] { failedChange }, new Exception("Connection failed"));
        }

        public int WriteBatchSize => 0;

        public IInterceptorSubject RootSubject => throw new NotSupportedException();

        public SourceState State => SourceState.Synchronizing;

        public DateTimeOffset? LastSynchronizedAt => null;

        public DateTimeOffset StateChangeTime { get; } = DateTimeOffset.UtcNow;

        public SourceDiagnostics Diagnostics { get; } = new(new SourceMetrics());

        ConnectorDiagnostics ISubjectConnector.Diagnostics => Diagnostics;

        public int PendingWriteCount => 0;

        public event EventHandler<SourceEvent>? StateChanged
        {
            add { }
            remove { }
        }

        public ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
        {
            return new ValueTask<WriteResult>(_result);
        }

        public Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static SubjectPropertyChange CreateChange(int id)
    {
        var subjectMock = new Mock<IInterceptorSubject>();
        return SubjectPropertyChange.Create(
            new PropertyReference(subjectMock.Object, $"Property{id}"),
            ChangeOrigin.Local,
            DateTimeOffset.UtcNow,
            null,
            id,
            id + 1);
    }

    private static ReadOnlyMemory<SubjectPropertyChange> CreateChanges(int count, int startId = 0)
    {
        var changes = new SubjectPropertyChange[count];
        for (var i = 0; i < count; i++)
        {
            changes[i] = CreateChange(startId + i);
        }
        return changes;
    }
}
