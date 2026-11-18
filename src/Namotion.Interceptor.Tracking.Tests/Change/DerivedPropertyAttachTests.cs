using System.Reactive.Concurrency;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;
using static Namotion.Interceptor.Tracking.Tests.Change.DerivedPropertyTestHelpers;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(PerPropertySubscriptionCollection.Name)]
public class DerivedPropertyAttachTests
{
    public DerivedPropertyAttachTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenASourceIsWrittenWhileItsSubjectAttachesBeforeTheDerivedPropertyIsAttached_ThenTheDerivedChangeIsPublished()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        var parent = new Person(context);
        var child = new Person();
        context.AddService<ILifecycleHandler>(new AttachingHook(child, () => child.FirstName = "Ann"));

        // Act
        parent.Father = child;

        // Assert: the structural write is announced first, then the changes its attach caused.
        Assert.Equal(
        [
            "Father: null -> Ann",
            "FirstName: null -> Ann",
            "FullName: NA -> Ann",
            "FullNameWithPrefix: Mr. NA -> Mr. Ann"
        ], changes.Select(Describe).ToArray());
    }

    [Fact]
    public void WhenAnotherThreadWritesASourceWhileItsSubjectAttaches_ThenTheDerivedChangeIsPublished()
    {
        // Arrange: a write of another thread does not wait for the attaching thread's lifecycle lock.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            lock (changes)
            {
                changes.Add(change);
            }
        });
        var parent = new Person(context);
        var child = new Person();
        var isWritten = false;
        context.AddService<ILifecycleHandler>(new AttachingHook(child, () =>
            isWritten = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => child.FirstName = "Ann").Wait(TestTimeouts.Default)));

        // Act
        parent.Father = child;

        // Assert
        Assert.True(isWritten);
        Assert.Contains("FullName: NA -> Ann", changes.Select(Describe));
        Assert.Contains("FullNameWithPrefix: Mr. NA -> Mr. Ann", changes.Select(Describe));
    }

    [Fact]
    public void WhenADerivedValueDoesNotChangeWhileItsSubjectAttaches_ThenTheAttachPublishesNoDerivedChange()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        var parent = new Person(context);
        var child = new Person { FirstName = "Ann" };

        // Act
        parent.Father = child;

        // Assert
        Assert.Equal(["Father: null -> Ann"], changes.Select(Describe).ToArray());
        Assert.Equal("Ann", child.FullName);
    }

    [Fact]
    public void WhenASourceWrittenWhileItsSubjectAttachesChangesTheDerivedValueBack_ThenTheAttachPublishesNoDerivedChange()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        var parent = new Person(context);
        var child = new Person { FirstName = "Ann" };
        context.AddService<ILifecycleHandler>(new AttachingHook(child, () =>
        {
            child.FirstName = "Bob";
            child.FirstName = "Ann";
        }));

        // Act
        parent.Father = child;

        // Assert
        Assert.Equal(
        [
            "Father: null -> Ann",
            "FirstName: Ann -> Bob",
            "FirstName: Bob -> Ann"
        ], changes.Select(Describe).ToArray());
    }

    [Fact]
    public void WhenASourceIsWrittenAfterTheDerivedValueWasPublishedOnAttach_ThenTheNextChangeStartsFromThePublishedValue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        var parent = new Person(context);
        var child = new Person();
        context.AddService<ILifecycleHandler>(new AttachingHook(child, () => child.FirstName = "Ann"));
        parent.Father = child;
        changes.Clear();

        // Act
        child.LastName = "Smith";

        // Assert
        Assert.Contains("FullName: Ann -> Ann Smith", changes.Select(Describe));
    }

    [Fact]
    public void WhenAGetterReadsItsParent_ThenTheAttachAndAMovePublishTheChange()
    {
        // Arrange: the graph state of the worker's ancestors is a tracked dependency, which the attach moves.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var first = new EvaluatorRoot(context) { Anchor = 1 };
        var second = new EvaluatorRoot(context) { Anchor = 2 };
        var machine = new EvaluatorMachine();
        machine.Worker.Evaluator = worker => worker.TryGetFirstParent<EvaluatorRoot>()?.Anchor ?? -1;
        var published = new List<(int OldValue, int NewValue)>();
        using var subscription = RecordResultChanges(machine, published);

        // Act
        first.Machine = machine;
        second.Machine = machine;
        first.Machine = null;

        // Assert
        Assert.Equal([(-1, 1), (1, 2)], published.ToArray());
    }

    [Fact]
    public void WhenAGetterBuildsAnArrayAndNothingItReadsIsWritten_ThenAttachesPublishNothingAndEvaluateTwiceEach()
    {
        // Arrange: an array has no value equality, so each evaluation differs from the one before the attach.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var evaluations = 0;
        machine.Worker.ObjectEvaluator = _ =>
        {
            evaluations++;
            return new[] { 1 };
        };
        var changes = SubscribeToChanges(context);

        // Act
        for (var attach = 0; attach < 3; attach++)
        {
            root.Machine = machine;
            root.Machine = null;
        }

        // Assert
        Assert.DoesNotContain(changes, change => change.Property.Name == nameof(EvaluatorWorker.ObjectResult));
        Assert.Equal(6, evaluations);
    }

    [Fact]
    public void WhenAGetterCountsItsEvaluationsAndNothingItReadsIsWritten_ThenTheAttachPublishesNothing()
    {
        // Arrange: the value comes from state that is not followed.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var counter = 0;
        machine.Worker.Evaluator = _ => ++counter;
        var published = new List<(int OldValue, int NewValue)>();
        using var subscription = RecordResultChanges(machine, published);

        // Act
        root.Machine = machine;

        // Assert
        Assert.Empty(published);
        Assert.Equal(2, counter);
    }

    [Fact]
    public void WhenTheGetterThrowsBeforeTheAttachAndASourceIsWrittenWhileItAttaches_ThenTheChangeStaysUnpublished()
    {
        // Arrange: without a parent the getter throws, so there is no value before the attach to publish the change
        // from (the documented gap).
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context) { Anchor = 1 };
        var machine = new EvaluatorMachine();
        machine.Worker.Evaluator = worker => worker.TryGetFirstParent<EvaluatorRoot>()!.Anchor;
        var published = new List<(int OldValue, int NewValue)>();
        using var subscription = RecordResultChanges(machine, published);
        context.AddService<ILifecycleHandler>(new AttachingHook(machine.Worker, () => root.Anchor = 2));

        // Act
        root.Machine = machine;

        // Assert
        Assert.Empty(published);
        Assert.Equal(2, machine.Worker.Result);
    }

    [Fact]
    public void WhenTheGetterThrowsAtALaterBaseline_ThenTheEarlierBaselineAndItsDependenciesAreCleared()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var isThrowing = false;
        machine.Worker.Evaluator = _ => isThrowing ? throw new InvalidOperationException("Not ready.") : root.Anchor;
        var result = new PropertyReference(machine.Worker, nameof(EvaluatorWorker.Result));
        DerivedPropertyOwnership.CaptureAttachBaseline(result);
        var hadBaseline = result.TryGetDerivedPropertyData()!.HasAttachBaseline;

        // Act
        isThrowing = true;
        DerivedPropertyOwnership.CaptureAttachBaseline(result);

        // Assert
        Assert.True(hadBaseline);
        Assert.False(result.TryGetDerivedPropertyData()!.HasAttachBaseline);
        Assert.Empty(result.GetRequiredProperties().ToArray());
        Assert.DoesNotContain(result, new PropertyReference(root, nameof(EvaluatorRoot.Anchor)).GetUsedByProperties().Items.ToArray());
    }

    [Fact]
    public void WhenAPropertyWithABaselineIsDetachedWithoutBeingAttached_ThenTheBaselineAndItsDependenciesAreCleared()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        machine.Worker.Evaluator = _ => root.Anchor;
        var result = new PropertyReference(machine.Worker, nameof(EvaluatorWorker.Result));
        DerivedPropertyOwnership.CaptureAttachBaseline(result);

        // Act
        DerivedPropertyOwnership.Detach(result, onlyIfSubjectIsDetached: false);

        // Assert
        Assert.False(result.TryGetDerivedPropertyData()!.HasAttachBaseline);
        Assert.DoesNotContain(result, new PropertyReference(root, nameof(EvaluatorRoot.Anchor)).GetUsedByProperties().Items.ToArray());
    }

    [Fact]
    public async Task WhenAnotherThreadWritesASourceWhileTheValueBeforeTheAttachIsEvaluated_ThenTheAttachPublishesTheChangeFromThatValue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context) { Anchor = 1 };
        var machine = new EvaluatorMachine();
        var gate = new EvaluationGate();
        machine.Worker.Evaluator = _ =>
        {
            var anchor = root.Anchor;
            gate.PauseAttachBaselineIfArmed();
            return anchor;
        };
        var published = new List<(int OldValue, int NewValue)>();
        using var subscription = RecordResultChanges(machine, published);
        gate.Arm();

        // Act
        var attach = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => root.Machine = machine);
        var isPaused = gate.Paused.Wait(TestTimeouts.Default);
        root.Anchor = 2;
        gate.Resume.Set();
        await attach.WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.True(isPaused);
        Assert.False(gate.HasTimedOut);
        Assert.Equal([(1, 2)], published.ToArray());
        Assert.Equal(2, machine.Worker.Result);
    }

    [Fact]
    public void WhenADetachThatKeepsAnAttachedSubjectRunsWhileTheSubjectAttaches_ThenTheAttachKeepsItsBaselineAndPublishesTheChange()
    {
        // Arrange: the detach stands for one of an earlier graph whose check found the subject detached before this
        // attach attached it again.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context) { Anchor = 1 };
        var machine = new EvaluatorMachine();
        machine.Worker.Evaluator = _ => root.Anchor;
        var result = new PropertyReference(machine.Worker, nameof(EvaluatorWorker.Result));
        var published = new List<(int OldValue, int NewValue)>();
        using var subscription = RecordResultChanges(machine, published);
        var hadBaseline = false;
        context.AddService<ILifecycleHandler>(new AttachingHook(machine.Worker, () =>
        {
            DerivedPropertyOwnership.Detach(result, onlyIfSubjectIsDetached: true);
            hadBaseline = result.TryGetDerivedPropertyData()!.HasAttachBaseline;
            root.Anchor = 2;
        }));

        // Act
        root.Machine = machine;

        // Assert
        Assert.True(hadBaseline);
        Assert.Equal([(1, 2)], published.ToArray());
    }

    [Fact]
    public void WhenAGetterEvaluatedBeforeItsAttachAttachesAnotherSubject_ThenOnlyTheBaselineEvaluationsAreReportedAsBaseline()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var otherRoot = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        var nestedMachine = new EvaluatorMachine();
        var outerEvaluations = new List<bool>();
        var nestedEvaluations = new List<bool>();
        nestedMachine.Worker.Evaluator = _ =>
        {
            nestedEvaluations.Add(IsAttachBaseline);
            return 0;
        };
        machine.Worker.Evaluator = _ =>
        {
            if (IsAttachBaseline && otherRoot.Machine is null)
            {
                otherRoot.Machine = nestedMachine;
            }

            outerEvaluations.Add(IsAttachBaseline);
            return 0;
        };

        // Act
        root.Machine = machine;

        // Assert
        Assert.Equal([true, false], outerEvaluations.ToArray());
        Assert.Equal([true, false], nestedEvaluations.ToArray());
    }

    private static IDisposable RecordResultChanges(EvaluatorMachine machine, List<(int OldValue, int NewValue)> published) =>
        new PropertyReference(machine.Worker, nameof(EvaluatorWorker.Result)).SubscribeInline((in SubjectPropertyChange change) =>
            published.Add((change.GetOldValue<int>(), change.GetNewValue<int>())));

    private static string Describe(SubjectPropertyChange change) =>
        $"{change.Property.Name}: {change.GetOldValue<object?>() ?? "null"} -> {change.GetNewValue<object?>() ?? "null"}";

    /// <summary>
    /// Runs an action once when the subject attaches, after context inheritance published its writes and before its
    /// properties are attached.
    /// </summary>
    [RunsAfter(typeof(ContextInheritanceHandler))]
    private sealed class AttachingHook(IInterceptorSubject subject, Action onAttach) : ILifecycleHandler
    {
        private bool _hasRun;

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.IsContextAttach && ReferenceEquals(change.Subject, subject) && !_hasRun)
            {
                _hasRun = true;
                onAttach();
            }
        }
    }
}
