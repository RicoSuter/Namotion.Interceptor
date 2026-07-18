using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(PerPropertySubscriptionCollection.Name)] // serialized; see Task 4
public class PerPropertySubscriptionTests
{
    [Fact]
    public void WhenPropertyChanges_ThenListenerIsInvokedByRef()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeNotifications();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        string? captured = null;
        using var subscription = property.Subscribe((in SubjectPropertyChange c) => captured = c.GetNewValue<string?>());

        // Act
        person.FirstName = "John";

        // Assert
        Assert.Equal("John", captured);
    }

    [Fact]
    public void WhenOtherPropertyChanges_ThenListenerIsNotInvoked()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeNotifications();
        var person = new Person(context);
        var invoked = false;
        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => invoked = true);

        // Act
        person.LastName = "Doe";

        // Assert
        Assert.False(invoked);
    }

    [Fact]
    public void WhenSubscriptionDisposed_ThenListenerStopsAndCountReturnsToZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeNotifications();
        var person = new Person(context);
        var count = 0;
        var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => count++);

        // Act
        person.FirstName = "John";
        subscription.Dispose();
        person.FirstName = "Jane";

        // Assert
        Assert.Equal(1, count);
        Assert.Equal(0, PropertyChangeSubscriptions.ReadLiveCount());
    }

    [Fact]
    public void WhenUnknownMember_ThenThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeNotifications();
        var person = new Person(context);

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            new PropertyReference(person, "DoesNotExist").Subscribe((in SubjectPropertyChange _) => { }));
    }
}

[CollectionDefinition(PerPropertySubscriptionCollection.Name, DisableParallelization = true)]
public class PerPropertySubscriptionCollection { public const string Name = "PerPropertySubscription"; }
