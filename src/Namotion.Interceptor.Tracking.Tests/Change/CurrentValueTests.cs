using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(PerPropertySubscriptionCollection.Name)]
public class CurrentValueTests
{
    [Fact]
    public void WhenNothingWrittenSinceTheChange_ThenGetCurrentValueEqualsGetNewValue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context);
        SubjectPropertyChange captured = default;
        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange change) => captured = change);

        // Act
        person.FirstName = "Rico";

        // Assert
        Assert.Equal(captured.GetNewValue<string>(), captured.GetCurrentValue<string>());
        Assert.Equal("Rico", captured.GetCurrentValue<string>());
    }

    [Fact]
    public void WhenPropertyWrittenAgainAfterTheChange_ThenGetCurrentValueReflectsTheLaterWrite()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context);
        SubjectPropertyChange captured = default;
        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange change) =>
            {
                if (captured.Property.Subject is null) captured = change;
            });
        person.FirstName = "Rico";

        // Act
        person.FirstName = "Suter";

        // Assert
        Assert.Equal("Rico", captured.GetNewValue<string>());
        Assert.Equal("Suter", captured.GetCurrentValue<string>());
    }
}
