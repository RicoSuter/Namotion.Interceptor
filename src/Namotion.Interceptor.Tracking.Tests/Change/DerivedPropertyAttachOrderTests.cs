using System.Reactive.Concurrency;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class DerivedPropertyAttachOrderTests
{
    [Fact]
    public void WhenSubtreeIsAssigned_ThenDerivedPropertyFollowsSiblingAttachedAfterIt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new SiblingReadingRoot(context);
        var machine = new SiblingReadingMachine();
        root.Machine = machine;
        var changes = SubscribeToChanges(context);

        // Act
        machine.Configuration.Installed = true;

        // Assert
        AssertSingleChange(changes, machine.Device, nameof(SiblingReadingDevice.IsInstalled), true);
    }

    [Fact]
    public void WhenRootIsAttachedToContext_ThenDerivedPropertyFollowsSiblingAttachedAfterIt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var machine = new SiblingReadingMachine();
        var root = new SiblingReadingRoot { Machine = machine };
        ((IInterceptorSubject)root).Context.AddFallbackContext(context);
        var changes = SubscribeToChanges(context);

        // Act
        machine.Configuration.Installed = true;

        // Assert
        AssertSingleChange(changes, machine.Device, nameof(SiblingReadingDevice.IsInstalled), true);
    }

    [Fact]
    public void WhenCollectionIsAssigned_ThenDerivedPropertyOfEveryItemFollowsSiblingAttachedAfterIt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new SiblingReadingRoot(context);
        var first = new SiblingReadingMachine();
        var second = new SiblingReadingMachine();
        root.Machines = [first, second];
        var changes = SubscribeToChanges(context);

        // Act
        first.Configuration.Installed = true;
        second.Configuration.Installed = true;

        // Assert
        AssertSingleChange(changes, first.Device, nameof(SiblingReadingDevice.IsInstalled), true);
        AssertSingleChange(changes, second.Device, nameof(SiblingReadingDevice.IsInstalled), true);
    }

    [Fact]
    public void WhenLaterSiblingIsWrittenDuringTheAttach_ThenDerivedPropertyPublishesTheChange()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new SiblingReadingRoot(context);
        var machine = new SiblingReadingMachine();
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change is { IsContextAttach: true, Subject: SiblingReadingConfiguration configuration })
            {
                configuration.Installed = true;
            }
        }));
        var changes = SubscribeToChanges(context);

        // Act
        root.Machine = machine;

        // Assert
        AssertSingleChange(changes, machine.Device, nameof(SiblingReadingDevice.IsInstalled), true);
    }

    [Fact]
    public void WhenLifecycleHandlerThrowsDuringTheAttach_ThenDerivedPropertyAttachedBeforeFollowsLaterSibling()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new SiblingReadingRoot(context);
        var machine = new SiblingReadingMachine();
        var throwingMachine = new SiblingReadingMachine();
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change.IsContextAttach && change.Subject == throwingMachine)
            {
                throw new InvalidOperationException();
            }
        }));
        Assert.Throws<InvalidOperationException>(() => root.Machines = [machine, throwingMachine]);
        var changes = SubscribeToChanges(context);

        // Act
        machine.Configuration.Installed = true;

        // Assert
        AssertSingleChange(changes, machine.Device, nameof(SiblingReadingDevice.IsInstalled), true);
    }

    [Fact]
    public void WhenFirstEvaluationThrows_ThenDerivedPropertyFollowsSiblingAttachedAfterIt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        machine.Worker.Evaluator = worker =>
        {
            // Throws in the first evaluation: the configuration has no parent before it is attached.
            var configuration = worker.TryGetFirstParent<EvaluatorMachine>()!.Configuration;
            return configuration.TryGetFirstParent<EvaluatorMachine>() is not null
                ? configuration.Value
                : throw new InvalidOperationException();
        };
        var changes = SubscribeToChanges(context);

        // Act
        root.Machine = machine;
        var changesOfAttach = changes.ToArray();
        machine.Configuration.Value = 5;

        // Assert: the evaluation that succeeds first gives the property its first value, which is not a change.
        Assert.DoesNotContain(changesOfAttach, change => change.Property.Subject == machine.Worker);
        AssertSingleChange(changes, machine.Worker, nameof(EvaluatorWorker.Result), 5);
    }

    [Fact]
    public void WhenSubtreeIsAssigned_ThenItIsAnnouncedBeforeItsChangedDerivedValue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();
        machine.Worker.Evaluator = worker =>
        {
            // Changes between the two evaluations: the configuration has no parent before it is attached.
            var configuration = worker.TryGetFirstParent<EvaluatorMachine>()!.Configuration;
            return configuration.TryGetFirstParent<EvaluatorMachine>() is not null ? 1 : 0;
        };
        var changes = SubscribeToChanges(context);

        // Act
        root.Machine = machine;

        // Assert
        Assert.Equal(2, changes.Count);
        Assert.Equal(new PropertyReference(root, nameof(EvaluatorRoot.Machine)), changes[0].Property);
        Assert.Equal(new PropertyReference(machine.Worker, nameof(EvaluatorWorker.Result)), changes[1].Property);
        Assert.Equal(1, changes[1].GetNewValue<int>());
        Assert.Equal(changes[0].ChangedTimestamp, changes[1].ChangedTimestamp);
    }

    [Fact]
    public void WhenDerivedPropertyReturnsNewArrayWithTheSameItems_ThenAttachPublishesNoChangeOfIt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new SiblingReadingRoot(context);
        var changes = SubscribeToChanges(context);

        // Act: the first machine is evaluated again, because the second one is attached after it.
        root.Machines = [new SiblingReadingMachine(), new SiblingReadingMachine()];

        // Assert
        Assert.DoesNotContain(changes, change => change.Property.Name == nameof(SiblingReadingMachine.InstalledStates));
    }

    [Fact]
    public void WhenDerivedPropertyIsRecalculatedDuringTheAttach_ThenItFollowsSiblingAttachedAfterIt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new EvaluatorRoot(context);
        var machine = new EvaluatorMachine();

        // Reads only a value at attach, and the later sibling once the anchor is set.
        machine.Worker.Evaluator = worker => root.Anchor == 0
            ? 0
            : 100 + worker.TryGetFirstParent<EvaluatorMachine>()!.Configuration.Value;
        context.WithService(() => new BeforeContextInheritanceLifecycleHandler(change =>
        {
            // The worker is attached, the configuration has no context yet.
            if (change.IsContextAttach && change.Subject == machine.Configuration)
            {
                root.Anchor = 1;
            }
        }));
        root.Machine = machine;
        var changes = SubscribeToChanges(context);

        // Act
        machine.Configuration.Value = 7;

        // Assert
        AssertSingleChange(changes, machine.Worker, nameof(EvaluatorWorker.Result), 107);
    }

    [Fact]
    public void WhenObserverThrowsOnChangedDerivedValue_ThenRemainingDerivedPropertiesStillPublish()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new SiblingReadingRoot(context);
        var first = new SiblingReadingMachine();
        var second = new SiblingReadingMachine();
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change is { IsContextAttach: true, Subject: SiblingReadingConfiguration configuration })
            {
                configuration.Installed = true;
            }
        }));

        // Subscribed first, because an observer that throws keeps later observers from seeing that change.
        var changes = SubscribeToChanges(context);
        using var throwingSubscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject == first.Device)
            {
                throw new InvalidOperationException();
            }
        });

        // Act
        var exception = Record.Exception(() => root.Machines = [first, second]);

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains(changes, change => change.Property.Subject == root && change.Property.Name == nameof(SiblingReadingRoot.Machines));
        AssertSingleChange(changes, second.Device, nameof(SiblingReadingDevice.IsInstalled), true);
    }

    private sealed class CallbackLifecycleHandler(Action<SubjectLifecycleChange> callback) : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            callback(change);
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

    private static List<SubjectPropertyChange> SubscribeToChanges(IInterceptorSubjectContext context)
    {
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        return changes;
    }

    private static void AssertSingleChange<T>(List<SubjectPropertyChange> changes, IInterceptorSubject subject, string propertyName, T newValue)
    {
        var change = Assert.Single(changes, c => c.Property.Subject == subject && c.Property.Name == propertyName);
        Assert.Equal(newValue, change.GetNewValue<T>());
    }
}
