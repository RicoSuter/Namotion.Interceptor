using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests;

/// <summary>
/// A non-partial [Derived] getter stores nothing, so the registry must not record it as a parent
/// of the subject it returns.
/// </summary>
public class DerivedGetterRegistryTests
{
    [Fact]
    public void WhenDerivedGetterReturnsAttachedSubject_ThenRegistryRecordsOneParent()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var holder = new DerivedSubjectHolder(context);
        var person = new Person { FirstName = "Child" };
        holder.Stored = person;

        // Act
        holder.SelectStored = true;

        // Assert
        var registeredPerson = person.TryGetRegisteredSubject()!;
        Assert.Equal(["Stored"], registeredPerson.Parents.Select(parent => parent.Property.Name));

        var selectedProperty = holder.TryGetRegisteredSubject()!
            .TryGetProperty(nameof(DerivedSubjectHolder.Selected))!;
        Assert.Empty(selectedProperty.Children);
    }

    [Fact]
    public void WhenOwnerOfDerivedGetterIsDetached_ThenSubjectIsRemovedFromRegistry()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new DerivedSubjectHolder(context);
        var holder = new DerivedSubjectHolder();
        var person = new Person { FirstName = "Child" };

        root.Child = holder;
        holder.Stored = person;
        holder.SelectStored = true;

        // Act
        root.Child = null;

        // Assert
        var registry = context.GetService<ISubjectRegistry>();
        Assert.DoesNotContain(person, registry.KnownSubjects.Keys);
    }
}
