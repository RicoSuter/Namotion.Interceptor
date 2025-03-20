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

        Assert.Equal("Mother", registeredSubjectProperty.GetValue());
    }
}