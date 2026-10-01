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
        var gate = new DataLookupGate();
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var subject = new GatedSubject(context, gate);
        var source = new object();
        var sourceWrites = new ConcurrentQueue<(string Property, string? Value)>();
        using var processor = CreateProcessor(context, source, sourceWrites, rule);
        using var cancellation = new CancellationTokenSource();
        var processing = processor.ProcessAsync(cancellation.Token);
        var property = new PropertyReference(subject, nameof(GatedSubject.Value));

        try
        {
            await WaitUntilProcessingAsync(subject, sourceWrites);

            // Act
            // The flush parks after reading the revision of the local write and before setting the flag.
            gate.Arm(lookup: 2, park: true);
            subject.Value = "L";
            Assert.True(gate.Reached.Wait(StepTimeout), "The flush never reached the published mark.");
            Assert.Contains("ChangeDeliveryFilter.TryAcceptForDelivery", gate.ReachingStack);

            // Whether the flush parked inside the subject lock decides whether the transaction can commit
            // before the mark, so ask the lock rather than infer it from thread state.
            var flushHoldsSubjectLock = !Monitor.TryEnter(subject.SyncRoot);
            if (!flushHoldsSubjectLock)
            {
                Monitor.Exit(subject.SyncRoot);
            }

            // A transaction writes to the source first, then applies locally as a confirmation.
            sourceWrites.Enqueue((nameof(GatedSubject.Value), "T"));
            var transactionApply = new Thread(() =>
            {
                using (PendingOrigin.Set(property, ChangeOrigin.Confirmed(source), "T"))
                {
                    subject.Value = "T";
                }
            })
            {
                IsBackground = true
            };
            transactionApply.Start();

            if (!flushHoldsSubjectLock)
            {
                // Nothing orders the commit after the mark, so let the dequeue judge the confirmation first.
                Assert.True(transactionApply.Join(StepTimeout), "The confirmation never committed.");
                CommitFenceConfirmation(subject, source);
                Assert.True(gate.FenceJudged.Wait(StepTimeout), "The dequeue loop never judged the fence confirmation.");
            }

            gate.Release.Set();
            Assert.True(transactionApply.Join(StepTimeout), "The confirmation never committed.");

            subject.Tail = "Tail";
            await AsyncTestHelpers.WaitUntilAsync(() => sourceWrites.Any(write => write is (nameof(GatedSubject.Tail), "Tail")));
        }
        finally
        {
            gate.Release.Set();
            await cancellation.CancelAsync();
            try { await processing; } catch (OperationCanceledException) { /* expected */ }
        }

        // Assert
        Assert.Equal("T", subject.Value);
        Assert.Equal(subject.Value, sourceWrites.Last(write => write.Property == nameof(GatedSubject.Value)).Value);
    }

    [Theory]
    [InlineData(ChangeDeliveryRule.SourceValuesMayBeStale)]
    [InlineData(ChangeDeliveryRule.SourceValuesAreSettled)]
    public async Task WhenAConfirmationCommitsBetweenTheFlushRevisionReadAndTheFirstPublishLock_ThenTheSourceEndsAtTheModelValue(ChangeDeliveryRule rule)
    {
        // Arrange
        var gate = new DataLookupGate();
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var subject = new GatedSubject(context, gate);
        var source = new object();
        var sourceWrites = new ConcurrentQueue<(string Property, string? Value)>();
        using var processor = CreateProcessor(context, source, sourceWrites, rule);
        using var cancellation = new CancellationTokenSource();
        var processing = processor.ProcessAsync(cancellation.Token);
        var property = new PropertyReference(subject, nameof(GatedSubject.Value));

        try
        {
            await WaitUntilProcessingAsync(subject, sourceWrites);

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
                Assert.Contains("ChangeDeliveryFilter.TryAcceptForDelivery", gate.ReachingStack);
                Assert.True(
                    SpinWait.SpinUntil(
                        () => (gate.ReachingThread!.ThreadState & ThreadState.WaitSleepJoin) != 0 && Monitor.LockContentionCount > contentionBefore,
                        StepTimeout),
                    "The flush never waited for the subject lock.");

                // The transaction writes to the source, then commits reentrantly while the flush waits, and
                // the dequeue judges its confirmation before the flag is set.
                sourceWrites.Enqueue((nameof(GatedSubject.Value), "T"));
                using (PendingOrigin.Set(property, ChangeOrigin.Confirmed(source), "T"))
                {
                    subject.Value = "T";
                }

                CommitFenceConfirmation(subject, source);
                Assert.True(gate.FenceJudged.Wait(StepTimeout), "The dequeue loop never judged the fence confirmation.");
            }
            finally
            {
                Monitor.Exit(subject.SyncRoot);
            }

            subject.Tail = "Tail";
            await AsyncTestHelpers.WaitUntilAsync(() => sourceWrites.Any(write => write is (nameof(GatedSubject.Tail), "Tail")));
        }
        finally
        {
            gate.Release.Set();
            await cancellation.CancelAsync();
            try { await processing; } catch (OperationCanceledException) { /* expected */ }
        }

        // Assert
        Assert.Equal("T", subject.Value);
        Assert.Equal(subject.Value, sourceWrites.Last(write => write.Property == nameof(GatedSubject.Value)).Value);
    }

    private static ChangeQueueProcessor CreateProcessor(
        IInterceptorSubjectContext context,
        object source,
        ConcurrentQueue<(string Property, string? Value)> sourceWrites,
        ChangeDeliveryRule rule)
    {
        return new ChangeQueueProcessor(
            source: source,
            context: context,
            propertyFilter: _ => true,
            writeHandler: (changes, _) =>
            {
                foreach (var change in changes.ToArray())
                {
                    sourceWrites.Enqueue((change.Property.Name, change.GetNewValue<string?>()));
                }

                return ValueTask.CompletedTask;
            },
            deliveryRule: rule,
            bufferTime: TimeSpan.FromMilliseconds(10),
            maxQueueDepth: null,
            logger: NullLogger.Instance);
    }

    /// <summary>
    /// Waits for a delivery, so the changes under test are not counted as queued before the processor started,
    /// which adds a write-state lookup on the dequeue thread.
    /// </summary>
    private static async Task WaitUntilProcessingAsync(GatedSubject subject, ConcurrentQueue<(string Property, string? Value)> sourceWrites)
    {
        subject.Tail = "Started";
        await AsyncTestHelpers.WaitUntilAsync(() => sourceWrites.Any(write => write.Property == nameof(GatedSubject.Tail)));
    }

    /// <summary>
    /// A confirmation on another property, queued behind the one under test. The dequeue loop judging it
    /// proves it has already judged everything before it.
    /// </summary>
    private static void CommitFenceConfirmation(GatedSubject subject, object source)
    {
        var fence = new PropertyReference(subject, nameof(GatedSubject.Other));
        using (PendingOrigin.Set(fence, ChangeOrigin.Confirmed(source), "Fence"))
        {
            subject.Other = "Fence";
        }
    }

    /// <summary>
    /// The subject's Data comparer. Signals, and optionally parks, on the Nth write-state lookup of
    /// <see cref="GatedSubject.Value"/> made by one thread other than the arming thread, and signals when
    /// such a thread looks up the write state of <see cref="GatedSubject.Other"/>.
    /// </summary>
    private sealed class DataLookupGate : IEqualityComparer<(string? property, string key)>
    {
        private readonly ThreadLocal<int> _lookups = new();
        private volatile int _armedLookup;
        private volatile bool _park;
        private volatile int _excludedThreadId = -1;

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
            if (obj.key == WriteStateKey &&
                _excludedThreadId != -1 &&
                Environment.CurrentManagedThreadId != _excludedThreadId)
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
        private IReadOnlyDictionary<string, SubjectPropertyMetadata>? _properties;
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

        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => _properties ?? DefaultProperties;

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

        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
        {
            lock (SyncRoot)
            {
                _properties = Properties
                    .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
                    .ToFrozenDictionary();
            }
        }

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
