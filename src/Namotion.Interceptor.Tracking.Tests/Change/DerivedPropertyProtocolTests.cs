using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

/// <summary>
/// Places another thread's write, attach or detach at a fixed point of a derived property's evaluation, commit or
/// publication, by parking the evaluating thread on a gate.
/// </summary>
[Collection(PerPropertySubscriptionCollection.Name)]
public class DerivedPropertyProtocolTests
{
    public DerivedPropertyProtocolTests() => PropertyChangeSubscriptions.ResetForTests();

    private static readonly TimeSpan Timeout = TestTimeouts.Default;

    [Fact]
    public async Task WhenWriteLandsBetweenReadAndLink_ThenEvaluationRunsAgain()
    {
        // Arrange: the getter reads Anchor only once Data is set, so Anchor is not linked when it is written.
        var context = CreateContext();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var gate = new EvaluationGate();
        machine.Worker.Evaluator = _ =>
        {
            if (root.Data is null)
            {
                return -1;
            }

            var anchor = root.Anchor;
            gate.PauseIfArmed();
            return anchor;
        };
        root.Machine = machine;
        var result = GetResult(machine);
        var published = new List<(int OldValue, int NewValue)>();
        using var subscription = RecordPublications(result, published);

        // Act
        gate.Arm();
        var recalculation = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => { root.Data = [1]; });
        var isPaused = gate.Paused.Wait(Timeout);
        root.Anchor = 5;
        gate.Resume.Set();
        await recalculation.WaitAsync(Timeout);

        // Assert
        Assert.True(isPaused);
        Assert.False(gate.HasTimedOut);
        Assert.Equal([(-1, 5)], published);
        Assert.Equal(5, machine.Worker.Result);
        Assert.Equal(5, result.TryGetDerivedPropertyData()?.LastKnownValue);
    }

    [Fact]
    public async Task WhenSourceIsDetachedWrittenAndAttachedAgainBetweenReadAndLink_ThenDerivedPropertyFollowsIt()
    {
        // Arrange: the getter reaches the spare configuration through a captured variable, not through Spare.
        var context = CreateContext();
        var root = new EvaluatorRoot(context);
        var spare = new EvaluatorConfiguration();
        root.Spare = spare;
        var machine = new EvaluatorMachine();
        var gate = new EvaluationGate();
        machine.Worker.Evaluator = _ =>
        {
            if (root.Anchor == 0)
            {
                return -1;
            }

            var value = spare.Value;
            gate.PauseIfArmed();
            return value;
        };
        root.Machine = machine;
        var result = GetResult(machine);
        var published = new List<(int OldValue, int NewValue)>();
        using var subscription = RecordPublications(result, published);

        // Act: the source is detached, written without a context and attached again on another thread, after
        // the getter read it and before the evaluation links it.
        gate.Arm();
        var recalculation = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => { root.Anchor = 1; });
        var isPaused = gate.Paused.Wait(Timeout);
        root.Spare = null;
        var isSourceDetached = !SubjectLifecycleAttachments.IsAttached(spare);
        spare.Value = 5;
        root.Spare = spare;
        gate.Resume.Set();
        await recalculation.WaitAsync(Timeout);
        spare.Value = 7;

        // Assert
        Assert.True(isPaused);
        Assert.False(gate.HasTimedOut);
        Assert.True(isSourceDetached);
        Assert.Equal([(-1, 5), (5, 7)], published);
        Assert.Equal(7, machine.Worker.Result);
    }

    [Fact]
    public async Task WhenAttachRunsWhileRecalculationEvaluates_ThenOwnershipIsHandedOffAndChangeIsPublishedAfterTheAttach()
    {
        // Arrange: the getter also reads state outside the model, which changes after the attach evaluation read it,
        // so only an evaluation after the attach sees it.
        var context = CreateContext();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var external = 0;
        var recalculationGate = new EvaluationGate();
        var attachGate = new EvaluationGate();
        machine.Worker.Evaluator = _ =>
        {
            var value = root.Anchor + Volatile.Read(ref external);
            recalculationGate.PauseIfArmed();
            attachGate.PauseIfArmed();
            return value;
        };
        root.Machine = machine;
        var result = GetResult(machine);
        var published = new List<(int OldValue, int NewValue)>();
        using var subscription = RecordPublications(result, published);

        // Act: a recalculation from before the detach evaluates the value 1 and reaches its commit while the
        // attach of the new attachment evaluates. The attach found the property owned by that recalculation, so it
        // set the request flag, which the recalculation hands off to the attach.
        recalculationGate.Arm();
        var recalculation = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => { root.Anchor = 1; });
        var isRecalculationPaused = recalculationGate.Paused.Wait(Timeout);
        root.Machine = null;
        root.Anchor = 2;
        attachGate.Arm();
        var attach = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => { root.Machine = machine; });
        var isAttachPaused = attachGate.Paused.Wait(Timeout);
        Volatile.Write(ref external, 100);
        recalculationGate.Resume.Set();
        var isRecalculationDone = await CompletesInTimeAsync(recalculation);
        attachGate.Resume.Set();
        await attach.WaitAsync(Timeout);

        // Assert: the attach commits its first value 2 without publishing it, then answers the request it took over.
        Assert.True(isRecalculationPaused);
        Assert.True(isAttachPaused);
        Assert.True(isRecalculationDone);
        Assert.False(recalculationGate.HasTimedOut || attachGate.HasTimedOut);
        Assert.Equal([(2, 102)], published);
        Assert.Equal(102, machine.Worker.Result);
        Assert.Equal(102, result.TryGetDerivedPropertyData()?.LastKnownValue);
    }

    [Fact]
    public async Task WhenAttachRunsWhileRecalculationPublishes_ThenPublicationsStayInCommitOrder()
    {
        // Arrange
        var context = CreateContext();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        machine.Worker.Evaluator = _ => root.Anchor;
        root.Machine = machine;
        var result = GetResult(machine);
        var gate = new EvaluationGate();
        var published = new List<(int OldValue, int NewValue)>();

        // Parks the delivery of the change to 1 before it is recorded.
        using var subscription = result.SubscribeInline((in SubjectPropertyChange change) =>
        {
            if (change.GetNewValue<int>() == 1)
            {
                gate.PauseIfArmed();
            }

            lock (published)
            {
                published.Add((change.GetOldValue<int>(), change.GetNewValue<int>()));
            }
        });

        // Act: while the change to 1 is delivered, the subject is detached, attached again with the value 2,
        // and 3 is written.
        gate.Arm();
        var recalculation = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => { root.Anchor = 1; });
        var isPaused = gate.Paused.Wait(Timeout);
        root.Machine = null;
        root.Anchor = 2;
        root.Machine = machine;
        root.Anchor = 3;
        gate.Resume.Set();
        await recalculation.WaitAsync(Timeout);

        // Assert
        Assert.True(isPaused);
        Assert.False(gate.HasTimedOut);
        Assert.Equal([(0, 1), (2, 3)], published);
        Assert.Equal(3, machine.Worker.Result);
    }

    [Fact]
    public async Task WhenRecalculationInFlightTakesOverAnotherThreadsWrite_ThenTheChangeCarriesThatWritesTimestamp()
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
        var secondTimestamp = firstTimestamp.AddMinutes(1);
        var published = new List<(int OldValue, int NewValue, DateTimeOffset Timestamp)>();
        using var subscription = result.SubscribeInline((in SubjectPropertyChange change) =>
        {
            lock (published)
            {
                published.Add((change.GetOldValue<int>(), change.GetNewValue<int>(), change.ChangedTimestamp));
            }
        });

        // Act: the recalculation of the write at the first timestamp pauses after it read Anchor, and the write at
        // the second timestamp is left to it.
        gate.Arm();
        var recalculation = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
        {
            using (SubjectChangeContext.WithChangedTimestamp(firstTimestamp))
            {
                root.Anchor = 1;
            }
        });
        var isPaused = gate.Paused.Wait(Timeout);
        using (SubjectChangeContext.WithChangedTimestamp(secondTimestamp))
        {
            root.Anchor = 2;
        }

        gate.Resume.Set();
        await recalculation.WaitAsync(Timeout);

        // Assert
        Assert.True(isPaused);
        Assert.False(gate.HasTimedOut);
        Assert.Equal([(0, 2, secondTimestamp)], published);
    }

    [Fact]
    public async Task WhenDerivedPropertyIsDetachedDuringItsEvaluation_ThenNothingIsPublished()
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
        var anchorProperty = new PropertyReference(root, nameof(EvaluatorRoot.Anchor));
        var published = new List<(int OldValue, int NewValue)>();
        using var subscription = RecordPublications(result, published);

        // Act
        gate.Arm();
        var recalculation = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => { root.Anchor = 1; });
        var isPaused = gate.Paused.Wait(Timeout);
        root.Machine = null;
        gate.Resume.Set();
        await recalculation.WaitAsync(Timeout);
        root.Anchor = 2;

        // Assert: the evaluation discards its result and its reads, so no link keeps the detached subject.
        Assert.True(isPaused);
        Assert.False(gate.HasTimedOut);
        Assert.Empty(published);
        Assert.Empty(result.GetRequiredProperties().ToArray());
        Assert.DoesNotContain(result, anchorProperty.GetUsedByProperties().Items.ToArray());
        Assert.Null(result.TryGetDerivedPropertyData()?.LastKnownValue);
    }

    [Fact]
    public async Task WhenTwoContextsAttachTheSameSubjectConcurrently_ThenDerivedPropertyIsAttachedOnce()
    {
        // Arrange: the getter also reads state outside the model, so a second evaluation would see it changed.
        var firstContext = CreateContext();
        var secondContext = CreateContext();
        var firstRoot = new EvaluatorRoot(firstContext);
        var secondHolder = new EvaluatorMachine(secondContext);
        var machine = new EvaluatorMachine();
        var worker = machine.Worker;
        var external = 1;
        var gate = new EvaluationGate();
        worker.Evaluator = _ =>
        {
            var value = machine.Configuration.Value * 10 + Volatile.Read(ref external);
            gate.PauseIfArmed();
            return value;
        };
        var result = GetResult(machine);
        var published = new List<(int OldValue, int NewValue)>();
        using var subscription = RecordPublications(result, published);

        // Act: the second context attaches the worker while the first one evaluates its derived property.
        gate.Arm();
        var firstAttach = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => { firstRoot.Machine = machine; });
        var isPaused = gate.Paused.Wait(Timeout);
        Volatile.Write(ref external, 2);
        var secondAttach = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => { secondHolder.Worker = worker; });
        var isSecondAttachDoneWhileFirstEvaluates = await CompletesInTimeAsync(secondAttach);
        gate.Resume.Set();
        await firstAttach.WaitAsync(Timeout);
        machine.Configuration.Value = 1;

        // Assert: the value of the first attach is kept, and the write publishes the change from it once.
        Assert.True(isPaused);
        Assert.False(gate.HasTimedOut);
        Assert.True(isSecondAttachDoneWhileFirstEvaluates);
        Assert.True(SubjectLifecycleAttachments.IsAttached(worker));
        Assert.Equal([(1, 12)], published);
        Assert.Equal(12, worker.Result);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void WhenGetterDetachesItsOwnSubjectDuringAttach_ThenAncestorKeepsNoLink(int detachingEvaluation)
    {
        // Arrange: for the second evaluation, another thread writes Anchor during the first one, so the attach
        // links Anchor and evaluates again.
        var context = CreateContext();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var worker = machine.Worker;
        var evaluations = 0;
        var isWrittenByOtherThread = true;
        worker.Evaluator = evaluatedWorker =>
        {
            var parent = evaluatedWorker.TryGetFirstParent<EvaluatorMachine>();
            var anchor = root.Anchor;
            var evaluation = Interlocked.Increment(ref evaluations);
            if (evaluation == 1 && detachingEvaluation == 2)
            {
                isWrittenByOtherThread = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => { root.Anchor = 1; }).Wait(Timeout);
            }

            if (evaluation == detachingEvaluation && parent is not null)
            {
                parent.Worker = new EvaluatorWorker();
            }

            return anchor;
        };
        var result = GetResult(machine);

        // Act
        root.Machine = machine;

        // Assert
        Assert.True(isWrittenByOtherThread);
        Assert.Equal(detachingEvaluation, evaluations);
        Assert.NotSame(worker, machine.Worker);
        Assert.DoesNotContain(result, new PropertyReference(root, nameof(EvaluatorRoot.Anchor)).GetUsedByProperties().Items.ToArray());
        Assert.Empty(result.GetRequiredProperties().ToArray());
    }

    [Fact]
    public async Task WhenAttachIsSupersededWhileItOwnsTheProperty_ThenPendingWriteIsPublished()
    {
        // Arrange
        var context = CreateContext();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var worker = machine.Worker;
        var gate = new EvaluationGate();
        worker.Evaluator = _ =>
        {
            var anchor = root.Anchor;
            gate.PauseIfArmed();
            return anchor;
        };
        var result = GetResult(machine);
        var published = new List<(int OldValue, int NewValue)>();
        using var subscription = RecordPublications(result, published);

        // Act: the attach owns the property and evaluates while a direct detach and attach supersede it, and a
        // write requests a recalculation that is left to it.
        gate.Arm();
        var attach = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => { root.Machine = machine; });
        var isPaused = gate.Paused.Wait(Timeout);
        worker.DetachSubjectProperty(result);
        root.Anchor = 1;
        worker.AttachSubjectProperty(result);
        root.Anchor = 2;
        gate.Resume.Set();
        await attach.WaitAsync(Timeout);

        // Assert: the newer attach committed 1, and the superseded attach recalculated the pending write.
        Assert.True(isPaused);
        Assert.False(gate.HasTimedOut);
        Assert.Equal([(1, 2)], published);
        Assert.Equal(2, worker.Result);
        Assert.Equal(2, result.TryGetDerivedPropertyData()?.LastKnownValue);
    }

    [Fact]
    public void WhenSubjectWithoutContextIsWrittenDuringAttach_ThenDerivedPropertyFollowsIt()
    {
        // Arrange: the worker attaches before the configuration and reads its value while it has no context.
        var context = CreateContext();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        machine.Worker.Evaluator = worker => worker.TryGetFirstParent<EvaluatorMachine>()!.Configuration.Value;
        var isWrittenWithoutContext = false;
        context.WithService(() => new BeforeContextInheritanceLifecycleHandler(change =>
        {
            if (change.IsContextAttach && change.Subject == machine.Configuration)
            {
                isWrittenWithoutContext = change.Subject.Context.TryGetService<LifecycleInterceptor>() is null;
                machine.Configuration.Value = 5;
            }
        }));
        var result = GetResult(machine);
        var published = new List<(int OldValue, int NewValue)>();
        using var subscription = RecordPublications(result, published);

        // Act
        root.Machine = machine;
        machine.Configuration.Value = 7;

        // Assert
        Assert.True(isWrittenWithoutContext);
        Assert.Equal([(0, 5), (5, 7)], published);
        Assert.Equal(7, machine.Worker.Result);
    }

    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
    }

    private static PropertyReference GetResult(EvaluatorMachine machine)
    {
        return new PropertyReference(machine.Worker, nameof(EvaluatorWorker.Result));
    }

    private static IDisposable RecordPublications(PropertyReference property, List<(int OldValue, int NewValue)> published)
    {
        return property.SubscribeInline((in SubjectPropertyChange change) =>
        {
            lock (published)
            {
                published.Add((change.GetOldValue<int>(), change.GetNewValue<int>()));
            }
        });
    }

    private static async Task<bool> CompletesInTimeAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Timeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Parks the first evaluation or delivery that passes it after <see cref="Arm"/> until resumed.</summary>
    private sealed class EvaluationGate
    {
        private int _isArmed;
        private volatile bool _hasTimedOut;

        public ManualResetEventSlim Paused { get; } = new();

        public ManualResetEventSlim Resume { get; } = new();

        public bool HasTimedOut => _hasTimedOut;

        public void Arm() => Volatile.Write(ref _isArmed, 1);

        public void PauseIfArmed()
        {
            if (Interlocked.Exchange(ref _isArmed, 0) == 1)
            {
                Paused.Set();
                _hasTimedOut = !Resume.Wait(Timeout);
            }
        }
    }

    [RunsBefore(typeof(ContextInheritanceHandler))]
    private sealed class BeforeContextInheritanceLifecycleHandler(Action<SubjectLifecycleChange> callback) : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            callback(change);
        }
    }
}
