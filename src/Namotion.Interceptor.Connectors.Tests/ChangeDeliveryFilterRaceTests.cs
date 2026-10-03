using System.Collections.Concurrent;
using System.Collections.Frozen;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

/// <summary>
/// A property's first publish races a transaction confirmation: the flush decides the older local write
/// while the confirmation commits and is judged as an echo. Whichever way it interleaves, the source has
/// to end at the model's value.
/// </summary>
public class ChangeDeliveryFilterRaceTests
{
    private const string WriteStateKey = "ni.wstate";
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(ChangeDeliveryRule.SourceValuesMayBeStale)]
    [InlineData(ChangeDeliveryRule.SourceValuesAreSettled)]
    public async Task WhenAConfirmationCommitsWhileTheFlushIsMarkingTheFirstPublish_ThenTheSourceEndsAtTheModelValue(ChangeDeliveryRule rule)
    {
        // Arrange
        await using var race = new Race(rule);
        var gate = race.Gate;
        var subject = race.Subject;

        // Act
        // The flush parks after reading the revision of the local write and before setting the flag.
        gate.Arm(lookup: 2, park: true);
        subject.Value = "L";
        Assert.True(gate.Reached.Wait(StepTimeout), "The flush never reached the published mark.");
        Assert.Contains("ChangeMerger.", gate.ReachingStack);

        // Whether the flush parked inside the subject lock decides whether the transaction can commit
        // before the mark, so ask the lock rather than infer it from thread state.
        var flushHoldsSubjectLock = !Monitor.TryEnter(subject.SyncRoot);
        if (!flushHoldsSubjectLock)
        {
            Monitor.Exit(subject.SyncRoot);
        }

        // A transaction writes to the source first, then applies locally as a confirmation.
        race.SourceWrites.Enqueue((nameof(GatedSubject.Value), "T"));
        var transactionApply = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(
            () => race.CommitConfirmation(nameof(GatedSubject.Value), "T"));

        if (!flushHoldsSubjectLock)
        {
            // Nothing orders the commit after the mark, so let the dequeue judge the confirmation first. Not
            // awaited: the fence has to be committed from the arming thread, which the gate ignores.
            Assert.True(SpinWait.SpinUntil(() => transactionApply.IsCompleted, StepTimeout), "The confirmation never committed.");
            race.CommitConfirmation(nameof(GatedSubject.Other), "Fence");
            Assert.True(gate.FenceJudged.Wait(StepTimeout), "The dequeue loop never judged the fence confirmation.");
        }

        gate.Release.Set();
        await transactionApply.WaitAsync(StepTimeout);
        await race.DeliverTailAsync("Tail");

        // Assert
        Assert.Equal("T", subject.Value);
        Assert.Equal(subject.Value, race.SourceWrites.Last(write => write.Property == nameof(GatedSubject.Value)).Value);
    }

    [Theory]
    [InlineData(ChangeDeliveryRule.SourceValuesMayBeStale)]
    [InlineData(ChangeDeliveryRule.SourceValuesAreSettled)]
    public async Task WhenAConfirmationCommitsBetweenTheFlushRevisionReadAndTheFirstPublishLock_ThenTheSourceEndsAtTheModelValue(ChangeDeliveryRule rule)
    {
        // Arrange
        await using var race = new Race(rule);
        var gate = race.Gate;
        var subject = race.Subject;

        // Act
        // Synchronous waits only while the monitor is held: an await could resume on another thread.
        gate.Arm(lookup: 1, park: false);
        Monitor.Enter(subject.SyncRoot);
        var contentionBefore = Monitor.LockContentionCount;
        try
        {
            subject.Value = "L";

            // The flush reads the revision of the local write lock-free, then waits for the subject lock.
            Assert.True(gate.Reached.Wait(StepTimeout), "The flush never read the revision.");
            Assert.Contains("ChangeMerger.", gate.ReachingStack);
            Assert.True(
                SpinWait.SpinUntil(
                    () => (gate.ReachingThread!.ThreadState & ThreadState.WaitSleepJoin) != 0 && Monitor.LockContentionCount > contentionBefore,
                    StepTimeout),
                "The flush never waited for the subject lock.");

            // The transaction writes to the source, then commits reentrantly while the flush waits, and
            // the dequeue judges its confirmation before the flag is set.
            race.SourceWrites.Enqueue((nameof(GatedSubject.Value), "T"));
            race.CommitConfirmation(nameof(GatedSubject.Value), "T");
            race.CommitConfirmation(nameof(GatedSubject.Other), "Fence");
            Assert.True(gate.FenceJudged.Wait(StepTimeout), "The dequeue loop never judged the fence confirmation.");
        }
        finally
        {
            Monitor.Exit(subject.SyncRoot);
        }

        await race.DeliverTailAsync("Tail");

        // Assert
        Assert.Equal("T", subject.Value);
        Assert.Equal(subject.Value, race.SourceWrites.Last(write => write.Property == nameof(GatedSubject.Value)).Value);
    }

    /// <summary>
    /// A gated subject with a running processor that records what reaches the source.
    /// </summary>
    private sealed class Race : IAsyncDisposable
    {
        private readonly ChangeQueueProcessor _processor;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _processing;
        private readonly object _source = new();

        public Race(ChangeDeliveryRule rule)
        {
            var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
            Subject = new GatedSubject(context, Gate);
            _processor = new ChangeQueueProcessor(
                source: _source,
                context: context,
                propertyFilter: _ => true,
                writeHandler: (changes, _) =>
                {
                    foreach (var change in changes.ToArray())
                    {
                        SourceWrites.Enqueue((change.Property.Name, change.GetNewValue<string?>()));
                    }

                    return ValueTask.CompletedTask;
                },
                deliveryRule: rule,
                bufferTime: TimeSpan.FromMilliseconds(10),
                maxQueueDepth: null,
                logger: NullLogger.Instance);
            _processing = _processor.ProcessAsync(_cancellation.Token);
        }

        public DataLookupGate Gate { get; } = new();

        public GatedSubject Subject { get; }

        public ConcurrentQueue<(string Property, string? Value)> SourceWrites { get; } = new();

        /// <summary>
        /// Applies a value locally as this source's transaction confirmation.
        /// </summary>
        public void CommitConfirmation(string propertyName, string value)
        {
            var property = new PropertyReference(Subject, propertyName);
            using (PendingOrigin.Set(property, ChangeOrigin.Confirmed(_source), value))
            {
                property.Metadata.SetValue!(Subject, value);
            }
        }

        /// <summary>
        /// Writes the tail marker and waits until it reaches the source, which orders every earlier write.
        /// </summary>
        public async Task DeliverTailAsync(string value)
        {
            Subject.Tail = value;
            await AsyncTestHelpers.WaitUntilAsync(() => SourceWrites.Any(write => write == (nameof(GatedSubject.Tail), value)));
        }

        public async ValueTask DisposeAsync()
        {
            Gate.Release.Set();
            await _cancellation.CancelAsync();
            try { await _processing; } catch (OperationCanceledException) { /* expected */ }

            _processor.Dispose();
            _cancellation.Dispose();
            Gate.Dispose();
        }
    }

    /// <summary>
    /// The subject's Data comparer. Signals, and optionally parks, on the Nth write-state lookup of
    /// <see cref="GatedSubject.Value"/> made by one thread other than the arming thread, and signals when
    /// such a thread looks up the write state of <see cref="GatedSubject.Other"/>.
    /// </summary>
    private sealed class DataLookupGate : IEqualityComparer<(string? property, string key)>, IDisposable
    {
        private readonly ThreadLocal<int> _lookups = new();
        private volatile int _armedLookup;
        private volatile bool _park;
        private volatile int _excludedThreadId;

        public ManualResetEventSlim Reached { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public ManualResetEventSlim FenceJudged { get; } = new();

        public Thread? ReachingThread { get; private set; }

        public string? ReachingStack { get; private set; }

        public void Arm(int lookup, bool park)
        {
            _excludedThreadId = Environment.CurrentManagedThreadId;
            _park = park;
            _armedLookup = lookup;
        }

        public bool Equals((string? property, string key) x, (string? property, string key) y) => x.Equals(y);

        public int GetHashCode((string? property, string key) obj)
        {
            if (obj.key == WriteStateKey && Environment.CurrentManagedThreadId != _excludedThreadId)
            {
                var armedLookup = _armedLookup;
                if (obj.property == nameof(GatedSubject.Value) && armedLookup != 0 && ++_lookups.Value == armedLookup)
                {
                    _armedLookup = 0;
                    ReachingThread = Thread.CurrentThread;
                    ReachingStack = Environment.StackTrace;
                    Reached.Set();
                    if (_park)
                    {
                        Release.Wait(StepTimeout * 2);
                    }
                }
                else if (obj.property == nameof(GatedSubject.Other))
                {
                    FenceJudged.Set();
                }
            }

            return obj.GetHashCode();
        }

        public void Dispose()
        {
            _lookups.Dispose();
            Reached.Dispose();
            Release.Dispose();
            FenceJudged.Dispose();
        }
    }

    /// <summary>
    /// A hand-written subject, because the generated one fixes its Data dictionary's comparer.
    /// </summary>
    private sealed class GatedSubject : IInterceptorSubject
    {
        private static readonly IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties =
            new Dictionary<string, SubjectPropertyMetadata>
            {
                [nameof(Value)] = Create(nameof(Value), o => ((GatedSubject)o).Value, (o, v) => ((GatedSubject)o).Value = (string?)v),
                [nameof(Other)] = Create(nameof(Other), o => ((GatedSubject)o).Other, (o, v) => ((GatedSubject)o).Other = (string?)v),
                [nameof(Tail)] = Create(nameof(Tail), o => ((GatedSubject)o).Tail, (o, v) => ((GatedSubject)o).Tail = (string?)v),
            }.ToFrozenDictionary();

        private IInterceptorExecutor? _executor;
        private string? _value;
        private string? _other;
        private string? _tail;

        public GatedSubject(IInterceptorSubjectContext context, IEqualityComparer<(string? property, string key)> dataComparer)
        {
            Data = new ConcurrentDictionary<(string? property, string key), object?>(dataComparer);
            ((IInterceptorSubject)this).Context.AddFallbackContext(context);
        }

        public ConcurrentDictionary<(string? property, string key), object?> Data { get; }

        public object SyncRoot { get; } = new();

        IInterceptorSubjectContext IInterceptorSubject.Context => InterceptorExecutor.GetOrCreate(ref _executor, this);

        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => DefaultProperties;

        public string? Value
        {
            get => _executor!.GetPropertyValue(nameof(Value), static o => ((GatedSubject)o)._value);
            set => _executor!.SetPropertyValue(nameof(Value), value, _value, static (o, v) => ((GatedSubject)o)._value = v);
        }

        public string? Other
        {
            get => _executor!.GetPropertyValue(nameof(Other), static o => ((GatedSubject)o)._other);
            set => _executor!.SetPropertyValue(nameof(Other), value, _other, static (o, v) => ((GatedSubject)o)._other = v);
        }

        public string? Tail
        {
            get => _executor!.GetPropertyValue(nameof(Tail), static o => ((GatedSubject)o)._tail);
            set => _executor!.SetPropertyValue(nameof(Tail), value, _tail, static (o, v) => ((GatedSubject)o)._tail = v);
        }

        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) => throw new NotSupportedException();

        private static SubjectPropertyMetadata Create(string name, Func<IInterceptorSubject, object?> getValue, Action<IInterceptorSubject, object?> setValue)
        {
            return new SubjectPropertyMetadata(
                typeof(GatedSubject).GetProperty(name)!,
                getValue,
                setValue,
                isIntercepted: true,
                isDynamic: false);
        }
    }
}
