using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;

namespace Namotion.Interceptor.Registry.Tests;

public class SubjectRegistryExtensionsTests
{
    [Fact]
    public void WhenResolvingRegisteredProperty_ThenItIsFound()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        // Act
        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = new Person
            {
                FirstName = "Mother",
                Mother = new Person
                {
                    FirstName = "Grandmother"
                }
            }
        };

        var registeredSubjectProperty = person.TryGetRegisteredProperty(p => p.Mother!.FirstName);
        var registeredSubject = person.TryGetRegisteredSubject();

        // Assert
        Assert.NotNull(registeredSubjectProperty);
        Assert.NotNull(registeredSubject);
        Assert.Equal(person.Mother, registeredSubjectProperty.Subject);

        Assert.Equal("Mother", registeredSubjectProperty.GetValue());
    }

    [Fact]
    public void WhenPropertyHasNestedAttributes_ThenGetAllAttributesReturnsAllRecursively()
    {
        // Arrange
        // Use Address.City instead of Person.FirstName because Person.FirstName has
        // pre-existing [MaxLength]/[PropertyAttribute] declarations that would pollute
        // the enumeration. Address.City is a plain writable string with no attributes.
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var address = new Address(context) { City = "Root" };

        var registered = address.TryGetRegisteredSubject()!;
        var city = registered.TryGetProperty("City")!;

        city.AddAttribute("Unit", typeof(string), _ => "m", null);
        var unit = city.Attributes.Single(a => a.AttributeName == "Unit");
        unit.AddAttribute("Source", typeof(string), _ => "sensor", null);

        // Act
        var names = city.GetAllAttributes().Select(a => a.AttributeName).ToArray();

        // Assert
        Assert.Equal(new[] { "Unit", "Source" }, names);
    }

    [Fact]
    public void WhenSubjectHasChildren_ThenGetAllPropertiesRecursesAndExcludesAttributes()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();

        var father = new Person { FirstName = "Father" };
        var child = new Person { FirstName = "Child" };
        var root = new Person(context)
        {
            FirstName = "Root",
            Father = father,
            Children = [child]
        };

        var registered = root.TryGetRegisteredSubject()!;
        registered.TryGetProperty("FirstName")!.AddAttribute("Unit", typeof(string), _ => "m", null);

        // Act
        var properties = registered.GetAllProperties().ToArray();

        // Assert — no RegisteredSubjectAttribute instances; children subject properties present
        Assert.DoesNotContain(properties, p => p is RegisteredSubjectAttribute);
        Assert.Contains(properties, p => p.Name == "FirstName" && p.Subject == root);
        Assert.Contains(properties, p => p.Name == "FirstName" && p.Subject == father);
        Assert.Contains(properties, p => p.Name == "FirstName" && p.Subject == child);
    }
}