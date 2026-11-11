using System.Reactive.Concurrency;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
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
    public void WhenSubtreeIsDetachedAndAttachedAgain_ThenDerivedPropertyFollowsSibling()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithParents();
        var root = new SiblingReadingRoot(context);
        var machine = new SiblingReadingMachine();
        root.Machine = machine;
        root.Machine = null;
        root.Machine = machine;
        var changes = SubscribeToChanges(context);

        // Act
        machine.Configuration.Installed = true;

        // Assert
        AssertSingleChange(changes, machine.Device, nameof(SiblingReadingDevice.IsInstalled), true);
    }

    [Fact]
    public void WhenSourceChangesLaterInTheSameAttach_ThenDerivedPropertyAttachedBeforePublishesTheChange()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var root = new Person(context);
        var first = new Person { FirstName = "A" };
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change is { IsContextAttach: true, Subject: Person { FirstName: "Writer" } })
            {
                first.FirstName = "B";
            }
        }));
        var changes = SubscribeToChanges(context);

        // Act
        root.Children = [first, new Person { FirstName = "Writer" }];

        // Assert
        AssertSingleChange(changes, first, nameof(Person.FullName), "B");
    }

    [Fact]
    public void WhenLifecycleHandlerThrowsDuringAttach_ThenDerivedPropertiesAttachedBeforeFollowTheirSources()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var root = new Person(context);
        var first = new Person { FirstName = "A" };
        context.WithService(() => new CallbackLifecycleHandler(change =>
        {
            if (change is { IsContextAttach: true, Subject: Person { FirstName: "Throw" } })
            {
                throw new InvalidOperationException();
            }
        }));
        Assert.Throws<InvalidOperationException>(() => root.Children = [first, new Person { FirstName = "Throw" }]);
        var changes = SubscribeToChanges(context);

        // Act
        first.FirstName = "B";

        // Assert
        AssertSingleChange(changes, first, nameof(Person.FullName), "B");
    }

    private sealed class CallbackLifecycleHandler(Action<SubjectLifecycleChange> callback) : ILifecycleHandler
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
