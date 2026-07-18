using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(PerPropertySubscriptionCollection.Name)]
public class SubscribeToPropertyResolverTests
{
    public SubscribeToPropertyResolverTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenDirectPropertySelector_ThenResolvesAndFires()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeNotifications();
        var person = new Person(context);
        string? captured = null;
        using var subscription = person.SubscribeToProperty(p => p.FirstName, (in SubjectPropertyChange c) => captured = c.GetNewValue<string?>());

        // Act
        person.FirstName = "John";

        // Assert
        Assert.Equal("John", captured);
    }

    [Fact]
    public void WhenChainedSelector_ThenThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeNotifications();
        var person = new Person(context) { Mother = new Person(context) };

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            person.SubscribeToProperty(p => p.Mother!.FirstName, (in SubjectPropertyChange _) => { }));
    }

    [Fact]
    public void WhenMethodSelector_ThenThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeNotifications();
        var person = new Person(context);

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            person.SubscribeToProperty(p => p.ToString(), (in SubjectPropertyChange _) => { }));
    }

    [Fact]
    public void WhenDerivedPropertySelector_ThenSubscribesWithoutThrowing()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeNotifications();
        var person = new Person(context);

        // Act
        using var subscription = person.SubscribeToProperty(p => p.FullName, (in SubjectPropertyChange _) => { });

        // Assert
        Assert.NotNull(subscription);
    }

    [Fact]
    public void WhenPropertyIsNeitherInterceptedNorDerived_ThenThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeNotifications();
        var holder = new PlainPropertyHolder(context);

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            holder.SubscribeToProperty(p => p.PlainProperty, (in SubjectPropertyChange _) => { }));
    }
}
