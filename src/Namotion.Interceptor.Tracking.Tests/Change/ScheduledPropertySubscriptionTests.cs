using System.Linq.Expressions;
using System.Reactive.Concurrency;

using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(PerPropertySubscriptionCollection.Name)]
public class ScheduledPropertySubscriptionTests
{
    public ScheduledPropertySubscriptionTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenSubscribedByTypedSelector_ThenChangesAreDeliveredOnTheScheduler()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();
        var received = new List<string?>();

        using var subscription = person.SubscribeToProperty(
            x => x.FirstName,
            (in SubjectPropertyChange change) => received.Add(change.GetNewValue<string?>()),
            scheduler);

        // Act
        person.FirstName = "Rico";

        // Assert
        Assert.Empty(received);
        scheduler.RunUntilIdle();
        Assert.Equal(["Rico"], received);
    }

    [Fact]
    public void WhenSelectorIsNotADirectPropertyAccess_ThenThrowsAndCountStaysZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => person.SubscribeToProperty(
            x => x.Father!.FirstName,
            (in SubjectPropertyChange _) => { },
            scheduler));
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenAnyScheduledSubscribeArgumentIsNull_ThenThrowsAndCountStaysZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var scheduler = new ControllableScheduler();

        // Act & Assert: a bare null is ambiguous between the observer and callback overloads, so each
        // needs its own cast, exactly as the unscheduled guard test does.
        Assert.Throws<ArgumentNullException>(() => property.Subscribe((IPropertyChangeObserver)null!, scheduler));
        Assert.Throws<ArgumentNullException>(() => property.Subscribe((PropertyChangeCallback)null!, scheduler));
        Assert.Throws<ArgumentNullException>(() => property.Subscribe((in SubjectPropertyChange _) => { }, null!));
        Assert.Throws<ArgumentNullException>(() => person.SubscribeToProperty(x => x.FirstName, (IPropertyChangeObserver)null!, scheduler));
        Assert.Throws<ArgumentNullException>(() => person.SubscribeToProperty(x => x.FirstName, (PropertyChangeCallback)null!, scheduler));
        Assert.Throws<ArgumentNullException>(() => person.SubscribeToProperty(x => x.FirstName, (in SubjectPropertyChange _) => { }, null!));
        Assert.Throws<ArgumentNullException>(() => ((Person)null!).SubscribeToProperty(x => x.FirstName, (in SubjectPropertyChange _) => { }, scheduler));
        Assert.Throws<ArgumentNullException>(() => person.SubscribeToProperty((Expression<Func<Person, string?>>)null!, (in SubjectPropertyChange _) => { }, scheduler));
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenSchedulerIsSynchronous_ThenThrowsAndCountStaysZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));

        // Act & Assert: both spellings are reference-equal to the singletons. The typed overloads are
        // asserted too, because they reject only by delegating, and a refactor reaching for the internal
        // Create would drop that delegation without failing any other test.
        Assert.Throws<ArgumentException>(() => property.Subscribe((in SubjectPropertyChange _) => { }, ImmediateScheduler.Instance));
        Assert.Throws<ArgumentException>(() => property.Subscribe((in SubjectPropertyChange _) => { }, Scheduler.Immediate));
        Assert.Throws<ArgumentException>(() => property.Subscribe((in SubjectPropertyChange _) => { }, CurrentThreadScheduler.Instance));
        Assert.Throws<ArgumentException>(() => property.Subscribe((in SubjectPropertyChange _) => { }, Scheduler.CurrentThread));
        Assert.Throws<ArgumentException>(() => person.SubscribeToProperty(x => x.FirstName, (in SubjectPropertyChange _) => { }, ImmediateScheduler.Instance));
        Assert.Throws<ArgumentException>(() => person.SubscribeToProperty(x => x.FirstName, (in SubjectPropertyChange _) => { }, CurrentThreadScheduler.Instance));
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenPropertyIsNotIntercepted_ThenThrowsAndCountStaysZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            new PropertyReference(person, "NotAProperty").Subscribe((in SubjectPropertyChange _) => { }, scheduler));
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }
}
