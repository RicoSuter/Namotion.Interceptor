using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class DerivedPropertyCleanupTests
{
    [Fact]
    public void WhenDerivedPropertyIsDetached_ThenItIsRemovedFromDependenciesUsedByProperties()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithLifecycle();

        var person = new Person(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        // Verify initial state - FullName depends on FirstName and LastName
        var fullNameProp = new PropertyReference(person, nameof(Person.FullName));
        var firstNameProp = new PropertyReference(person, nameof(Person.FirstName));
        var lastNameProp = new PropertyReference(person, nameof(Person.LastName));

        // FullName should be in FirstName's UsedByProperties
        Assert.Contains(fullNameProp, firstNameProp.GetUsedByProperties().Items.ToArray());
        Assert.Contains(fullNameProp, lastNameProp.GetUsedByProperties().Items.ToArray());

        // Act - Detach the FullName property (simulating subject detachment)
        person.DetachSubjectProperty(fullNameProp);

        // Assert - FullName should be removed from both dependencies' UsedByProperties
        Assert.DoesNotContain(fullNameProp, firstNameProp.GetUsedByProperties().Items.ToArray());
        Assert.DoesNotContain(fullNameProp, lastNameProp.GetUsedByProperties().Items.ToArray());
    }

    [Fact]
    public void WhenSourcePropertyIsDetached_ThenItIsRemovedFromDerivedPropertiesRequiredProperties()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithLifecycle();

        var person = new Person(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        var fullNameProp = new PropertyReference(person, nameof(Person.FullName));
        var firstNameProp = new PropertyReference(person, nameof(Person.FirstName));

        // FullName depends on FirstName
        Assert.Contains(firstNameProp, fullNameProp.GetRequiredProperties().Items.ToArray());

        // Act - Detach the FirstName property (simulating partial cleanup)
        person.DetachSubjectProperty(firstNameProp);

        // Assert - FirstName should be removed from FullName's RequiredProperties
        Assert.DoesNotContain(firstNameProp, fullNameProp.GetRequiredProperties().Items.ToArray());
    }

    [Fact]
    public void WhenSubjectWithDerivedPropertyIsDetached_ThenCrossSubjectReferencesAreCleaned()
    {
        // Arrange - Car.AveragePressure depends on Tire.Pressure from multiple tires
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithLifecycle()
            .WithContextInheritance();

        var car = new Car(context);
        var tire0 = car.Tires[0];

        // Get property references
        var averagePressureProp = new PropertyReference(car, nameof(Car.AveragePressure));
        var tire0PressureProp = new PropertyReference(tire0, nameof(Tire.Pressure));

        // Verify initial state - AveragePressure depends on Tire.Pressure
        Assert.Contains(tire0PressureProp, averagePressureProp.GetRequiredProperties().Items.ToArray());

        // Act - Replace the entire Tires array to trigger property write interceptor
        // Note: Array indexer assignment (car.Tires[0] = x) doesn't trigger interception
        var newTires = car.Tires.ToArray();
        newTires[0] = new Tire(context);
        car.Tires = newTires;

        // Assert - The derived property's RequiredProperties should no longer reference the old tire
        // This is what prevents memory leaks (Car not keeping old Tire alive)
        Assert.DoesNotContain(tire0PressureProp, averagePressureProp.GetRequiredProperties().Items.ToArray());

        // Note: tire0PressureProp.UsedByProperties may still contain stale entries (for performance).
        // These don't cause memory leaks and will be GC'd with the detached subject.
    }

    [Fact]
    public void DerivedPropertyDependingOnDerivedProperty_IsNotTracked()
    {
        // Note: This is a known design limitation.
        // Derived properties that depend on OTHER derived properties don't go through
        // the interceptor chain (the getter is a direct C# property access).
        // Only partial (intercepted) properties are tracked as dependencies.

        // Arrange - FullNameWithPrefix depends on FullName (another derived property)
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithLifecycle();

        var person = new Person(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        var fullNameProp = new PropertyReference(person, nameof(Person.FullName));

        // Assert - FullName.UsedByProperties is empty because derived-to-derived deps aren't tracked
        Assert.Empty(fullNameProp.GetUsedByProperties().Items.ToArray());
    }

    [Fact]
    public void WhenDerivedPropertyIsDetached_ThenItRemovesItselfFromDependenciesUsedByProperties()
    {
        // This is Case 1: When a derived property is detached, it removes itself from
        // all its dependencies' UsedByProperties to break backward references.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithLifecycle()
            .WithContextInheritance();

        var car = new Car(context);
        var tire0 = car.Tires[0];

        var averagePressureProp = new PropertyReference(car, nameof(Car.AveragePressure));
        var tire0PressureProp = new PropertyReference(tire0, nameof(Tire.Pressure));

        // Verify initial state - AveragePressure is in Tire[0].Pressure.UsedByProperties
        Assert.Contains(averagePressureProp, tire0PressureProp.GetUsedByProperties().Items.ToArray());

        // Also verify RequiredProperties
        Assert.Contains(tire0PressureProp, averagePressureProp.GetRequiredProperties().Items.ToArray());

        // Act - Detach just the AveragePressure property (simulates derived property detachment)
        car.DetachSubjectProperty(averagePressureProp);

        // Assert - AveragePressure should be removed from tire's UsedByProperties (Case 1)
        Assert.DoesNotContain(averagePressureProp, tire0PressureProp.GetUsedByProperties().Items.ToArray());
    }
}
