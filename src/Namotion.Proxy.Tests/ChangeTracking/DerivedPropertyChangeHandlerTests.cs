using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Abstractions;
using Namotion.Interceptor.Validation;

namespace Namotion.Proxy.Tests.ChangeTracking;

public class DerivedPropertyChangeHandlerTests
{
    [Fact]
    public void WhenChangingPropertyWhichIsUsedInDerivedProperty_ThenDerivedPropertyIsChanged()
    {
        // Arrange
        var changes = new List<PropertyChangedContext>();
        var context = InterceptorCollection
            .Create()
            .WithDerivedPropertyChangeDetection();

        context
            .GetPropertyChangedObservable()
            .Subscribe(changes.Add);

        // Act
        var person = new Person(context)
        {
            FirstName = "Rico",
            LastName = "Suter"
        };

        // Assert
        Assert.Contains(changes, c =>
            c.Property.Name == nameof(Person.FullName) &&
            c.OldValue?.ToString() == " " &&
            c.NewValue?.ToString() == "Rico ");

        Assert.Contains(changes, c =>
            c.Property.Name == nameof(Person.FullName) &&
            c.OldValue?.ToString() == "Rico " &&
            c.NewValue?.ToString() == "Rico Suter");

        Assert.Contains(changes, c =>
            c.Property.Name == nameof(Person.FullNameWithPrefix) &&
            c.NewValue?.ToString() == "Mr. Rico Suter");
    }
}