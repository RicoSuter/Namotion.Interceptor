using System.Collections.Concurrent;
using System.Diagnostics;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// The topology gate is held across the whole write chain, so code running inside it that dispatches
/// structural work to another thread and waits for that thread deadlocks: the holder cannot release
/// the gate until the work finishes and the work cannot start until the gate is released. The
/// dispatch goes through a thread, a pool or a queue and is invisible from here, so this cannot be
/// rejected up front the way a second gate can. A waiter watches the holder instead, which turns the
/// hang into a named exception on the dispatched thread and leaves the holder free to finish.
/// </summary>
/// <remarks>
/// One test per way into the gate, because each reaches it through different code: a downstream
/// interceptor, the two handler kinds, the subject event, and a structural getter the lifecycle
/// itself invokes. They pin the report rather than the hang, so a return to waiting forever fails on
/// the bounded join instead of taking the suite with it.
///
/// The last test observes a contender waiting at the gate while its holder remains runnable, then
/// releases the holder and verifies that the write completes afterwards. It covers overlap and
/// serialization, without asserting a minimum runnable hold duration.
///
/// The two tests after it guard against false positives: a holder that is blocked but guaranteed to
/// make progress, on an ordinary lock or on an unattached subject's attachment monitor, must not be
/// convicted, so an innocent contending write still succeeds.
/// </remarks>
public class SameContextGateDeadlockTests
{
    /// <summary>Comfortably longer than the conviction threshold, so a hang is still reported as one.</summary>
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Well under the last-resort bound, so a case decided by that bound instead of by
    /// watching the holder fails these.</summary>
    private static readonly TimeSpan FastFailureBudget = TimeSpan.FromSeconds(5);

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenAGateHolderWaitsForStructuralWorkItDispatched_ThenTheDispatchedWriteFailsInsteadOfHanging()
    {
        // Arrange: a write interceptor downstream of the lifecycle runs while the gate is held, and
        // from there dispatches a structural write on the same context to a worker it then joins.
        var context = CreateContextConvictingQuickly();

        var workerTarget = new Person { FirstName = "worker target" };
        ((IInterceptorSubject)workerTarget).AttachToContext(context);

        var interceptor = new DispatchAndJoinOnceInterceptor(
            () => workerTarget.Father = new Person { FirstName = "fromWorker" },
            JoinTimeout);
        context.AddService<IWriteInterceptor>(interceptor);

        var trigger = new Person { FirstName = "trigger" };
        ((IInterceptorSubject)trigger).AttachToContext(context);

        // Act
        var stopwatch = Stopwatch.StartNew();
        trigger.Mother = new Person { FirstName = "fromHolder" };
        stopwatch.Stop();

        // Assert: the worker gave up rather than waiting forever, and said what the caller did.
        Assert.True(interceptor.WorkerJoined, "the dispatched worker never returned, so the gate wait is unbounded");
        AssertReportedAsDeadlock(interceptor.WorkerException, stopwatch.Elapsed);

        // Assert: only the dispatched write failed. The holder's own write went through, and the
        // subject the worker could not attach is untouched.
        Assert.Equal("fromHolder", trigger.Mother.FirstName);
        Assert.Null(workerTarget.Father);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenALifecycleHandlerWaitsForStructuralWorkItDispatched_ThenTheDispatchedWriteFails()
    {
        // Arrange
        var context = CreateContextConvictingQuickly();

        var workerTarget = new Person(context);
        var trigger = new Person { FirstName = "trigger" };
        Exception? workerException = null;
        var stopwatch = new Stopwatch();
        context.WithService(() => new DelegateLifecycleHandler(change =>
        {
            if (change.IsContextAttach && ReferenceEquals(change.Subject, trigger))
            {
                workerException = DispatchAndJoin(() => workerTarget.Father = new Person(), stopwatch);
            }
        }), _ => false);

        // Act
        ((IInterceptorSubject)trigger).AttachToContext(context);

        // Assert
        AssertReportedAsDeadlock(workerException, stopwatch.Elapsed);
        Assert.Null(workerTarget.Father);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenAPropertyHandlerWaitsForStructuralWorkItDispatched_ThenTheDispatchedWriteFails()
    {
        // Arrange
        var context = CreateContextConvictingQuickly();

        var workerTarget = new Person(context);
        var trigger = new Person { FirstName = "trigger" };
        Exception? workerException = null;
        var stopwatch = new Stopwatch();
        context.WithService(() => new DelegatePropertyAttachHandler(change =>
        {
            if (change.Property.Name == nameof(Person.FirstName) && ReferenceEquals(change.Subject, trigger))
            {
                workerException = DispatchAndJoin(() => workerTarget.Mother = new Person(), stopwatch);
            }
        }), _ => false);

        // Act
        ((IInterceptorSubject)trigger).AttachToContext(context);

        // Assert
        AssertReportedAsDeadlock(workerException, stopwatch.Elapsed);
        Assert.Null(workerTarget.Mother);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenASubjectAttachedHandlerWaitsForStructuralWorkItDispatched_ThenTheDispatchedWriteFails()
    {
        // Arrange
        var context = CreateContextConvictingQuickly();

        var workerTarget = new Person(context);
        var trigger = new Person { FirstName = "trigger" };
        Exception? workerException = null;
        var stopwatch = new Stopwatch();
        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, trigger))
            {
                workerException = DispatchAndJoin(() => workerTarget.Father = new Person(), stopwatch);
            }
        };

        // Act
        ((IInterceptorSubject)trigger).AttachToContext(context);

        // Assert
        AssertReportedAsDeadlock(workerException, stopwatch.Elapsed);
        Assert.Null(workerTarget.Father);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenAStructuralGetterWaitsForStructuralWorkItDispatched_ThenTheDispatchedWriteFails()
    {
        // Arrange: the explicit attach reads the candidate's structural properties under the gate,
        // so the getter is the one entry point no interceptor ordering can move.
        var context = CreateContextConvictingQuickly();

        var workerTarget = new Person(context) { FirstName = "worker target" };
        var candidate = new Person { FirstName = "candidate" };
        Exception? workerException = null;
        var getterCalls = 0;
        var stopwatch = new Stopwatch();
        ((IInterceptorSubject)candidate).AddProperties(new SubjectPropertyMetadata(
            "Captured", typeof(Person), [], _ =>
            {
                if (++getterCalls == 1)
                {
                    workerException = DispatchAndJoin(() => workerTarget.Father = new Person(), stopwatch);
                }

                return null;
            }, null, isIntercepted: true, isDynamic: true));

        // Act
        ((IInterceptorSubject)candidate).AttachToContext(context);

        // Assert
        AssertReportedAsDeadlock(workerException, stopwatch.Elapsed);
        Assert.Null(workerTarget.Father);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenAnActiveGateHolderOverlapsAContendingWrite_ThenTheWriteCompletesAfterTheHolderLeaves()
    {
        // Arrange
        var context = CreateContextConvictingQuickly().WithRegistry();
        var contendedTarget = new Person();
        contendedTarget.AttachToContext(context);
        var root = new Person();
        var child = new Person();
        using var holderEntered = new ManualResetEventSlim();
        using var releaseHolder = new ManualResetEventSlim();
        using var contenderStarted = new ManualResetEventSlim();
        using var contenderCompleted = new ManualResetEventSlim();
        var holderLeft = false;
        var contenderObservedHolderLeft = false;
        ((IInterceptorSubject)root).AddProperties(new SubjectPropertyMetadata(
            "HoldGate", typeof(Person), [], _ =>
            {
                if (root.TryGetContext() is not null && !holderEntered.IsSet)
                {
                    holderEntered.Set();
                    // A runnable holder is distinct from the blocked holders the deadlock detector rejects.
                    while (!releaseHolder.IsSet) Thread.SpinWait(64);
                    Volatile.Write(ref holderLeft, true);
                }
                return null;
            }, null, isIntercepted: true, isDynamic: true));
        Exception? attachException = null;
        Exception? contendedException = null;
        var attacher = new Thread(() => attachException = Record.Exception(() => root.AttachToContext(context)))
            { IsBackground = true };
        var contender = new Thread(() =>
        {
            contenderStarted.Set();
            contendedException = Record.Exception(() => contendedTarget.Father = child);
            contenderObservedHolderLeft = Volatile.Read(ref holderLeft);
            contenderCompleted.Set();
        }) { IsBackground = true };

        // Act
        attacher.Start();
        try
        {
            Assert.True(holderEntered.Wait(JoinTimeout), "the attach never entered its structural getter");
            contender.Start();
            Assert.True(contenderStarted.Wait(JoinTimeout));
            Assert.True(SpinWait.SpinUntil(
                () => (contender.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0 || contenderCompleted.IsSet,
                JoinTimeout), "the contender never reached the held gate");
            Assert.False(contenderCompleted.IsSet, "the write completed while another thread still held the gate");
        }
        finally
        {
            releaseHolder.Set();
        }

        // Assert
        Assert.True(attacher.Join(JoinTimeout));
        Assert.True(contender.Join(JoinTimeout));
        Assert.Null(attachException);
        Assert.Null(contendedException);
        Assert.True(contenderObservedHolderLeft);
        Assert.Same(child, contendedTarget.Father);
        SupportContractAssertions.Settled(context, [root, contendedTarget], root, contendedTarget, child);
        root.DetachFromContext(context);
        contendedTarget.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [], root, contendedTarget, child);
    }

    /// <summary>
    /// Three threads, no cycle, no dispatch-and-wait. The gate holder blocks on an ordinary lock
    /// held by a thread that never touches the lifecycle and always releases it. The system is
    /// guaranteed to make progress, so the contending write must eventually succeed.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S2925", Justification = "The sleep is the scenario, not a wait: a third thread holds what the gate holder blocks on for a bounded time, so the holder is blocked yet guaranteed to progress.")]
    public void WhenTheGateHolderBlocksOnALockThatIsGuaranteedToBeReleased_ThenTheContendingWriteStillSucceeds()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var contendedTarget = new Person { FirstName = "target" };
        ((IInterceptorSubject)contendedTarget).AttachToContext(context);

        var unrelatedLock = new object();
        var holderIsInsideCallback = new ManualResetEventSlim();
        var lockTaken = new ManualResetEventSlim();

        // A third thread that has nothing to do with the lifecycle holds the lock for 600ms.
        var lockHolder = new Thread(() =>
        {
            lock (unrelatedLock)
            {
                lockTaken.Set();
                Thread.Sleep(600);
            }
        }) { IsBackground = true };
        lockHolder.Start();
        Assert.True(lockTaken.Wait(TimeSpan.FromSeconds(10)));

        var trigger = new Person { FirstName = "trigger" };
        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, trigger))
            {
                holderIsInsideCallback.Set();
                lock (unrelatedLock)
                {
                    // Entering is the point: the holder blocks here until the third thread releases.
                }
            }
        };

        Exception? attachException = null;
        var attacher = new Thread(() =>
            attachException = Record.Exception(() => ((IInterceptorSubject)trigger).AttachToContext(context)))
        { IsBackground = true };

        // Act
        attacher.Start();
        Assert.True(holderIsInsideCallback.Wait(TimeSpan.FromSeconds(10)), "the holder never entered the callback");

        var stopwatch = Stopwatch.StartNew();
        var contendedException = Record.Exception(() => contendedTarget.Father = new Person { FirstName = "waited" });
        stopwatch.Stop();

        Assert.True(attacher.Join(TimeSpan.FromSeconds(30)), "the attach never finished");
        Assert.True(lockHolder.Join(TimeSpan.FromSeconds(30)));

        // Assert: nothing here is a deadlock. The lock holder always releases, the attach always
        // completes, so the contending write must go through rather than be convicted.
        Assert.Null(attachException);
        Assert.Null(contendedException);
        Assert.Equal("waited", contendedTarget.Father!.FirstName);
    }

    /// <summary>
    /// The same shape but with no user callback at all: the holder blocks inside the framework's own
    /// claim step, on the attachment monitor of a subject a third thread is legitimately writing to
    /// while it is unattached (which holds that monitor across the whole write, see
    /// InterceptorExecutor.SetStructuralPropertyValue's lifecycle-free arm).
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S2925", Justification = "The sleep is the scenario, not a wait: a third thread holds what the gate holder blocks on for a bounded time, so the holder is blocked yet guaranteed to progress.")]
    public void WhenTheGateHolderBlocksOnAnUnattachedSubjectsAttachmentMonitor_ThenTheContendingWriteStillSucceeds()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var contendedTarget = new Person { FirstName = "target" };
        ((IInterceptorSubject)contendedTarget).AttachToContext(context);

        var slowWriteStarted = new ManualResetEventSlim();
        var candidate = new SlowSetterSubject(() =>
        {
            slowWriteStarted.Set();
            Thread.Sleep(600);
        });

        // A third thread writes a structural property of an UNATTACHED subject. That runs under the
        // subject's attachment monitor for the whole (slow) write, violating no documented contract.
        var slowWriter = new Thread(() => candidate.Child = new Person()) { IsBackground = true };
        slowWriter.Start();
        Assert.True(slowWriteStarted.Wait(TimeSpan.FromSeconds(10)), "the slow write never started");

        // The gate holder attaches that same subject: it takes the gate, then blocks on the very
        // attachment monitor the slow write holds.
        var root = new Person { FirstName = "root" };
        ((IInterceptorSubject)root).AttachToContext(context);
        Exception? attachException = null;
        var attacher = new Thread(() =>
            attachException = Record.Exception(() => ((IInterceptorSubject)candidate).AttachToContext(context)))
        { IsBackground = true };

        // Act
        attacher.Start();

        // Wait for the state the scenario needs rather than for a duration: the attacher has to be
        // parked on the attachment monitor before the contending write starts, or the write races
        // ahead of the hold and proves nothing.
        Assert.True(
            SpinWait.SpinUntil(
                () => (attacher.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(10)),
            "the attacher never blocked, so it never held the gate against the monitor");

        var contendedException = Record.Exception(() => contendedTarget.Father = new Person { FirstName = "waited" });

        Assert.True(attacher.Join(TimeSpan.FromSeconds(30)), "the attach never finished");
        Assert.True(slowWriter.Join(TimeSpan.FromSeconds(30)));

        // Assert
        Assert.Null(attachException);
        Assert.Null(contendedException);
    }

    /// <summary>Runs one structural write on a worker thread and returns what waiting for the gate cost it.</summary>
    private static Exception? DispatchAndJoin(Action structuralWrite, Stopwatch stopwatch)
    {
        Exception? exception = null;
        var worker = new Thread(() => exception = Record.Exception(structuralWrite)) { IsBackground = true };
        stopwatch.Start();
        worker.Start();
        var joined = worker.Join(JoinTimeout);
        stopwatch.Stop();
        Assert.True(joined, "the dispatched worker never returned, so the gate wait is unbounded");
        return exception;
    }

    /// <summary>
    /// A context whose gate convicts a continuously blocked holder in milliseconds rather than in
    /// the production threshold, so these cases cost their dispatch rather than that whole bound.
    /// </summary>
    private static IInterceptorSubjectContext CreateContextConvictingQuickly()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        ((LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!)
            .BlockedHolderThresholdMilliseconds = 200;

        return context;
    }

    private static void AssertReportedAsDeadlock(Exception? workerException, TimeSpan elapsed)
    {
        var violation = Assert.IsType<LifecycleContractViolationException>(workerException);
        Assert.Contains("topology gate", violation.Message);
        Assert.Contains("never once seen running", violation.Message);
        Assert.Contains("dispatched structural work to another thread", violation.Message);
        Assert.Contains("Nothing was read and nothing was changed", violation.Message);
        Assert.True(elapsed < FastFailureBudget,
            $"the deadlock took {elapsed} to report, so the holder check did not decide it");
    }

    /// <summary>Dispatches one structural write to a worker thread and waits for it, once.</summary>
    private sealed class DispatchAndJoinOnceInterceptor(Action dispatchedWrite, TimeSpan joinTimeout) : IWriteInterceptor
    {
        private bool _fired;

        public bool WorkerJoined { get; private set; }

        public Exception? WorkerException { get; private set; }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (!_fired)
            {
                _fired = true;
                var worker = new Thread(() => WorkerException = Record.Exception(dispatchedWrite)) { IsBackground = true };
                worker.Start();
                WorkerJoined = worker.Join(joinTimeout);
            }

            next(ref context);
        }
    }

    private sealed class SlowSetterSubject : IInterceptorSubject
    {
        private static readonly Action<SlowSetterSubject> WriteHook =
            subject => subject.OnWrite();

        private readonly Dictionary<string, SubjectPropertyMetadata> _metadata;
        private readonly Action _onWrite;
        private IInterceptorExecutor? _executor;
        private Person? _child;

        public SlowSetterSubject(Action onWrite)
        {
            _onWrite = onWrite;
            _metadata = new Dictionary<string, SubjectPropertyMetadata>
            {
                [nameof(Child)] = new(
                    nameof(Child),
                    typeof(Person),
                    [],
                    static subject => ((SlowSetterSubject)subject)._child,
                    static (subject, value) => ((SlowSetterSubject)subject).Child = (Person?)value,
                    isIntercepted: true,
                    isDynamic: false)
            };
        }

        public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);

        public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => _metadata;

        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) =>
            throw new NotSupportedException();

        private void OnWrite() => _onWrite();

        public Person? Child
        {
            get => Executor.GetPropertyValue(nameof(Child), static subject => ((SlowSetterSubject)subject)._child);
            set => Executor.SetPropertyValue(nameof(Child), value, _child,
                static (subject, newValue) =>
                {
                    var typed = (SlowSetterSubject)subject;
                    WriteHook(typed);
                    typed._child = newValue;
                });
        }
    }
}
