using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Derived;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Derived;

/// <summary>
/// The change that answers the requests left to an owner carries the newest of their timestamps and of the request the
/// owner carries unanswered from the current attachment, whichever request arrives last.
/// </summary>
[Collection(PerPropertySubscriptionCollection.Name)]
public class DerivedPropertyNewestTimestampTests
{
    public DerivedPropertyNewestTimestampTests() => PropertyChangeSubscriptions.ResetForTests();

    private static readonly TimeSpan Timeout = TestTimeouts.Default;

    [Fact]
    public async Task WhenQueuedRequestWithAnOlderTimestampRunsWhileARecalculationIsInFlight_ThenTheChangeCarriesTheNewerTimestamp()
    {
        // Arrange
        var context = CreateContext();
        var lifecycle = context.GetService<LifecycleInterceptor>();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var gate = new EvaluationGate();
        machine.Worker.Evaluator = _ =>
        {
            var anchor = root.Anchor;
            gate.PauseIfArmed();
            return anchor;
        };
        root.Machine = machine;
        var result = GetResult(machine);
        var firstTimestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var queuedTimestamp = firstTimestamp.AddMinutes(1);
        var newerTimestamp = firstTimestamp.AddMinutes(2);
        var published = new List<(int OldValue, int NewValue, DateTimeOffset Timestamp)>();
        using var subscription = result.SubscribeInline((in SubjectPropertyChange change) =>
        {
            lock (published)
            {
                published.Add((change.GetOldValue<int>(), change.GetNewValue<int>(), change.ChangedTimestamp));
            }
        });

        // Act: a recalculation pauses after it read Anchor. Under the lifecycle lock, a write at the queued timestamp
        // queues its request, and another thread's write at the newer timestamp is left to the recalculation. The
        // release of the lock drains the queued request while the recalculation is still in flight.
        gate.Arm();
        var recalculation = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
        {
            using (SubjectChangeContext.WithChangedTimestamp(firstTimestamp))
            {
                root.Anchor = 1;
            }
        });
        var isPaused = gate.Paused.Wait(Timeout);
        var isNewerWritten = false;
        var hasRun = lifecycle.TryRunWhileAttached(root, () =>
        {
            using (SubjectChangeContext.WithChangedTimestamp(queuedTimestamp))
            {
                root.Anchor = 2;
            }

            isNewerWritten = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
            {
                using (SubjectChangeContext.WithChangedTimestamp(newerTimestamp))
                {
                    root.Anchor = 3;
                }
            }).Wait(Timeout);
        });

        gate.Resume.Set();
        await recalculation.WaitAsync(Timeout);

        // Assert
        Assert.True(isPaused);
        Assert.True(hasRun);
        Assert.True(isNewerWritten);
        Assert.False(gate.HasTimedOut);
        Assert.Equal([(0, 3, newerTimestamp)], published);
    }

    [Fact]
    public async Task WhenQueuedRequestWithAnOlderTimestampRunsAfterTheRecalculationAdoptedANewerOne_ThenTheChangeCarriesTheNewerTimestamp()
    {
        // Arrange
        var context = CreateContext();
        var lifecycle = context.GetService<LifecycleInterceptor>();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var firstGate = new EvaluationGate();
        var secondGate = new EvaluationGate();
        machine.Worker.Evaluator = _ =>
        {
            // The second gate is passed first, so the evaluation that the first gate parks does not reach it.
            var anchor = root.Anchor;
            secondGate.PauseIfArmed();
            firstGate.PauseIfArmed();
            return anchor;
        };
        root.Machine = machine;
        var result = GetResult(machine);
        var firstTimestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var queuedTimestamp = firstTimestamp.AddMinutes(1);
        var newerTimestamp = firstTimestamp.AddMinutes(2);
        var published = new List<(int OldValue, int NewValue, DateTimeOffset Timestamp)>();
        using var subscription = result.SubscribeInline((in SubjectPropertyChange change) =>
        {
            lock (published)
            {
                published.Add((change.GetOldValue<int>(), change.GetNewValue<int>(), change.ChangedTimestamp));
            }
        });

        // Act: a recalculation pauses after it read Anchor. Under the lifecycle lock, a write at the queued timestamp
        // queues its request, and another thread's write at the newer timestamp is left to the recalculation, which
        // discards its evaluation, adopts the newer timestamp and pauses again in its next evaluation. The release of
        // the lock drains the queued request while that evaluation is in flight.
        firstGate.Arm();
        var recalculation = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
        {
            using (SubjectChangeContext.WithChangedTimestamp(firstTimestamp))
            {
                root.Anchor = 1;
            }
        });
        var isFirstPaused = firstGate.Paused.Wait(Timeout);
        var isNewerWritten = false;
        var isSecondPaused = false;
        var hasRun = lifecycle.TryRunWhileAttached(root, () =>
        {
            using (SubjectChangeContext.WithChangedTimestamp(queuedTimestamp))
            {
                root.Anchor = 2;
            }

            isNewerWritten = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
            {
                using (SubjectChangeContext.WithChangedTimestamp(newerTimestamp))
                {
                    root.Anchor = 3;
                }
            }).Wait(Timeout);

            secondGate.Arm();
            firstGate.Resume.Set();
            isSecondPaused = secondGate.Paused.Wait(Timeout);
        });

        secondGate.Resume.Set();
        await recalculation.WaitAsync(Timeout);

        // Assert
        Assert.True(isFirstPaused);
        Assert.True(hasRun);
        Assert.True(isNewerWritten);
        Assert.True(isSecondPaused);
        Assert.False(firstGate.HasTimedOut);
        Assert.False(secondGate.HasTimedOut);
        Assert.Equal([(0, 3, newerTimestamp)], published);
    }

    [Fact]
    public async Task WhenAnotherThreadWritesAnOlderTimestampWhileANewerRequestIsPending_ThenTheChangeCarriesTheNewerTimestamp()
    {
        // Arrange
        var context = CreateContext();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var gate = new EvaluationGate();
        machine.Worker.Evaluator = _ =>
        {
            var anchor = root.Anchor;
            gate.PauseIfArmed();
            return anchor;
        };
        root.Machine = machine;
        var result = GetResult(machine);
        var firstTimestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var olderTimestamp = firstTimestamp.AddMinutes(1);
        var newerTimestamp = firstTimestamp.AddMinutes(2);
        var published = new List<(int OldValue, int NewValue, DateTimeOffset Timestamp)>();
        using var subscription = result.SubscribeInline((in SubjectPropertyChange change) =>
        {
            lock (published)
            {
                published.Add((change.GetOldValue<int>(), change.GetNewValue<int>(), change.ChangedTimestamp));
            }
        });

        // Act: a recalculation pauses after it read Anchor. Writes from this thread leave their requests to it: first
        // one at the newer timestamp, then one at an explicit older timestamp.
        gate.Arm();
        var recalculation = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
        {
            using (SubjectChangeContext.WithChangedTimestamp(firstTimestamp))
            {
                root.Anchor = 1;
            }
        });
        var isPaused = gate.Paused.Wait(Timeout);
        using (SubjectChangeContext.WithChangedTimestamp(newerTimestamp))
        {
            root.Anchor = 2;
        }

        using (SubjectChangeContext.WithChangedTimestamp(olderTimestamp))
        {
            root.Anchor = 3;
        }

        gate.Resume.Set();
        await recalculation.WaitAsync(Timeout);

        // Assert
        Assert.True(isPaused);
        Assert.False(gate.HasTimedOut);
        Assert.Equal([(0, 3, newerTimestamp)], published);
    }

    [Fact]
    public async Task WhenAttachCommitsWhileARecalculationWithANewerTimestampFromBeforeTheDetachOwnsTheProperty_ThenTheChangeCarriesTheAttachTimestamp()
    {
        // Arrange: the getter also reads state outside the model, which changes after the attach evaluation read it,
        // so the recalculation that answers the attach publishes a change.
        var context = CreateContext();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var external = 0;
        var gate = new EvaluationGate();
        machine.Worker.Evaluator = _ =>
        {
            var value = root.Anchor + Volatile.Read(ref external);
            gate.PauseIfArmed();
            return value;
        };
        root.Machine = machine;
        var result = GetResult(machine);
        var attachTimestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var writeTimestamp = attachTimestamp.AddYears(4);
        var published = new List<(int OldValue, int NewValue, DateTimeOffset Timestamp)>();
        using var subscription = result.SubscribeInline((in SubjectPropertyChange change) =>
        {
            lock (published)
            {
                published.Add((change.GetOldValue<int>(), change.GetNewValue<int>(), change.ChangedTimestamp));
            }
        });

        // Act: the recalculation of the write at the write timestamp, which is newer than the attach timestamp,
        // pauses in the getter while the subject is detached and attached again at the attach timestamp; the attach
        // commits while that recalculation still owns the property and leaves its request to it.
        gate.Arm();
        var recalculation = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
        {
            using (SubjectChangeContext.WithChangedTimestamp(writeTimestamp))
            {
                root.Anchor = 1;
            }
        });
        var isPaused = gate.Paused.Wait(Timeout);
        root.Machine = null;
        using (SubjectChangeContext.WithChangedTimestamp(attachTimestamp))
        {
            root.Machine = machine;
        }

        Volatile.Write(ref external, 100);
        gate.Resume.Set();
        await recalculation.WaitAsync(Timeout);

        // Assert
        Assert.True(isPaused);
        Assert.False(gate.HasTimedOut);
        Assert.Equal([(1, 101, attachTimestamp)], published);
        Assert.Equal(101, machine.Worker.Result);
    }

    [Fact]
    public async Task WhenOtherThreadWritesAnOlderTimestampAfterTheLoopReachedTheIterationLimit_ThenTheTakeoverChangeCarriesTheNewerTimestamp()
    {
        // Arrange: while the getter writes the source it reads, at the newer timestamp, no evaluation is stable, so
        // the owner loop ends at the iteration limit and carries that timestamp unanswered.
        var context = CreateContext();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var isWritingSource = false;
        var firstTimestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var olderTimestamp = firstTimestamp.AddMinutes(1);
        var newerTimestamp = firstTimestamp.AddMinutes(2);
        machine.Worker.Evaluator = _ =>
        {
            var anchor = root.Anchor;
            if (Volatile.Read(ref isWritingSource))
            {
                using (SubjectChangeContext.WithChangedTimestamp(newerTimestamp))
                {
                    root.Anchor = anchor + 1;
                }
            }

            return anchor;
        };
        root.Machine = machine;
        var result = GetResult(machine);
        var published = new List<(int OldValue, int NewValue, DateTimeOffset Timestamp)>();
        using var subscription = result.SubscribeInline((in SubjectPropertyChange change) =>
        {
            lock (published)
            {
                published.Add((change.GetOldValue<int>(), change.GetNewValue<int>(), change.ChangedTimestamp));
            }
        });

        // The iteration limit warning is traced after the loop's last lock and before its takeover: there the
        // getter stops writing, and another thread writes the source at the older timestamp, a foreign request
        // that the takeover answers together with the carried one.
        var ownerThreadId = 0;
        var isOlderWritten = false;
        using var listener = new CallbackTraceListener(message =>
        {
            if (message.Contains("exhausted for") &&
                Environment.CurrentManagedThreadId == Volatile.Read(ref ownerThreadId) &&
                Volatile.Read(ref isWritingSource))
            {
                Volatile.Write(ref isWritingSource, false);
                isOlderWritten = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
                {
                    using (SubjectChangeContext.WithChangedTimestamp(olderTimestamp))
                    {
                        root.Anchor = 1000;
                    }
                }).Wait(Timeout);
            }
        });

        // Act
        var recalculation = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
        {
            Volatile.Write(ref ownerThreadId, Environment.CurrentManagedThreadId);
            Volatile.Write(ref isWritingSource, true);
            using (SubjectChangeContext.WithChangedTimestamp(firstTimestamp))
            {
                root.Anchor = 1;
            }
        });
        await recalculation.WaitAsync(Timeout);

        // Assert
        Assert.True(isOlderWritten);
        Assert.Equal([(0, 1000, newerTimestamp)], published);
        Assert.Equal(1000, machine.Worker.Result);
    }

    [Fact]
    public async Task WhenAttachCommitsAfterTheLoopReachedTheIterationLimitAndItsRequestArrivesAfterTheTakeoverDecided_ThenTheChangeCarriesTheAttachTimestamp()
    {
        // Arrange: another thread writes the source the getter reads during its first evaluation, and the getter
        // writes it itself at every later one, so no evaluation is stable: the owner loop ends at the iteration limit
        // and carries the foreign request unanswered. The getter also reads state outside the model, which changes
        // after the attach evaluation read it, so the recalculation that answers the attach publishes a change.
        var context = CreateContext();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var isWritingSource = false;
        var isForeignWriteDone = false;
        var external = 0;
        var firstTimestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var attachTimestamp = firstTimestamp.AddMinutes(1);
        machine.Worker.Evaluator = _ =>
        {
            var anchor = root.Anchor;
            if (Volatile.Read(ref isWritingSource))
            {
                if (isForeignWriteDone)
                {
                    root.Anchor = anchor + 1;
                }
                else
                {
                    isForeignWriteDone = true;
                    DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => { root.Anchor = anchor + 1; }).Wait(Timeout);
                }
            }

            return anchor + Volatile.Read(ref external);
        };
        root.Machine = machine;
        var result = GetResult(machine);
        var published = new List<(int OldValue, int NewValue, DateTimeOffset Timestamp)>();
        using var subscription = result.SubscribeInline((in SubjectPropertyChange change) =>
        {
            lock (published)
            {
                published.Add((change.GetOldValue<int>(), change.GetNewValue<int>(), change.ChangedTimestamp));
            }
        });

        // The iteration limit warning is traced after the loop's last lock and before its takeover: there the writes
        // stop, and another thread detaches the subject and attaches it again at the attach timestamp. That attach
        // commits and then parks before it requests the recalculation after it, until the owner thread is done.
        var ownerThreadId = 0;
        var attachThreadId = 0;
        var isAttachParkArmed = 0;
        Task? reattach = null;
        var isAttachCommitted = false;
        using var isAttachParked = new ManualResetEventSlim();
        using var resumeAttach = new ManualResetEventSlim();
        var isAttachResumed = false;
        using var listener = new CallbackTraceListener(message =>
        {
            if (message.Contains("exhausted for") &&
                Environment.CurrentManagedThreadId == Volatile.Read(ref ownerThreadId) &&
                Volatile.Read(ref isWritingSource))
            {
                Volatile.Write(ref isWritingSource, false);
                reattach = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
                {
                    Volatile.Write(ref attachThreadId, Environment.CurrentManagedThreadId);
                    root.Machine = null;
                    Volatile.Write(ref isAttachParkArmed, 1);
                    using (SubjectChangeContext.WithChangedTimestamp(attachTimestamp))
                    {
                        root.Machine = machine;
                    }
                });
                isAttachCommitted = isAttachParked.Wait(Timeout);
            }
        });

        DerivedPropertyOwnership.AfterTakeForTests = property =>
        {
            if (property == result &&
                Environment.CurrentManagedThreadId == Volatile.Read(ref attachThreadId) &&
                Interlocked.Exchange(ref isAttachParkArmed, 0) == 1)
            {
                Volatile.Write(ref external, 100);
                isAttachParked.Set();
                isAttachResumed = resumeAttach.Wait(Timeout);
            }
        };
        try
        {
            // Act
            var recalculation = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
            {
                Volatile.Write(ref ownerThreadId, Environment.CurrentManagedThreadId);
                Volatile.Write(ref isWritingSource, true);
                using (SubjectChangeContext.WithChangedTimestamp(firstTimestamp))
                {
                    root.Anchor = 1;
                }
            });
            await recalculation.WaitAsync(Timeout);
            resumeAttach.Set();
            Assert.NotNull(reattach);
            await reattach.WaitAsync(Timeout);
        }
        finally
        {
            resumeAttach.Set();
            DerivedPropertyOwnership.AfterTakeForTests = null;
        }

        // Assert
        var anchor = root.Anchor;
        Assert.True(isAttachCommitted);
        Assert.True(isAttachResumed);
        Assert.Equal([(anchor, anchor + 100, attachTimestamp)], published);
        Assert.Equal(anchor + 100, machine.Worker.Result);
    }

    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
    }

    private static PropertyReference GetResult(EvaluatorMachine machine)
    {
        return new PropertyReference(machine.Worker, nameof(EvaluatorWorker.Result));
    }
}
