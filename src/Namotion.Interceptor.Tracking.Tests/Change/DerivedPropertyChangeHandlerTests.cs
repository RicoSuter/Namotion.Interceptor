using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class DerivedPropertyChangeHandlerTests
{
    [Fact]
    public void WhenChangingPropertyWhichIsUsedInDerivedProperty_ThenDerivedPropertyIsChanged()
    {
        // Arrange
        var changes = new List<PropertyChangedContext>();
        var context = InterceptorSubjectContext
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

    [Fact]
    public void WhenTrackingDerivedPropertiesUsingPropertiesFromOtherSubjectsAndInheritance_ThenChangesAreTracked()
    {
        // Arrange
        var changes = new List<PropertyChangedContext>();
        var context = InterceptorSubjectContext
            .Create()
            .WithInterceptorInheritance()
            .WithDerivedPropertyChangeDetection();
        
        // Act
        var car = new Car(context);
        
        context
            .GetPropertyChangedObservable()
            .Subscribe(changes.Add);
        
        car.Tires[0].Pressure = 2.0m;       
        
        // Assert
        Assert.Contains(changes, c => c.Property.Name == "AveragePressure");
    }
}