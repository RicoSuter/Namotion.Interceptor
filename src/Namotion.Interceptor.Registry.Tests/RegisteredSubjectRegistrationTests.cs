using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;

namespace Namotion.Interceptor.Registry.Tests;

public class RegisteredSubjectRegistrationTests
{
    [Fact]
    public void WhenSubjectsAreAttached_ThenTheirInstancesAreRegistered()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();

        // Act
        var person = new Person(context) { Mother = new Person() };

        // Assert
        Assert.True(person.TryGetRegisteredSubject()!.IsRegistered);
        Assert.True(person.Mother.TryGetRegisteredSubject()!.IsRegistered);
    }

    [Fact]
    public void WhenSubjectIsDetached_ThenItsInstanceAndThoseOfTheSubjectsBelowItAreNoLongerRegistered()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var grandmother = new Person();
        var mother = new Person { Mother = grandmother };
        var person = new Person(context) { Mother = mother };
        var registeredMother = mother.TryGetRegisteredSubject()!;
        var registeredGrandmother = grandmother.TryGetRegisteredSubject()!;

        // Act
        person.Mother = null;

        // Assert
        Assert.False(registeredMother.IsRegistered);
        Assert.False(registeredGrandmother.IsRegistered);
        Assert.Empty(registeredMother.Parents);
        Assert.True(person.TryGetRegisteredSubject()!.IsRegistered);
    }

    [Fact]
    public void WhenSubjectIsAttachedAgain_ThenItIsRegisteredAsANewInstanceAndTheOldOneStaysUnregistered()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var mother = new Person();
        var person = new Person(context) { Mother = mother };
        var firstRegistration = mother.TryGetRegisteredSubject()!;
        person.Mother = null;

        // Act
        person.Mother = mother;

        // Assert
        var secondRegistration = mother.TryGetRegisteredSubject()!;
        Assert.NotSame(firstRegistration, secondRegistration);
        Assert.True(secondRegistration.IsRegistered);
        Assert.False(firstRegistration.IsRegistered);
    }

    [Fact]
    public void WhenRegisteredSubjectIsCreatedOutsideTheRegistry_ThenItIsNotRegistered()
    {
        // Act
        var registeredSubject = new RegisteredSubject(new Person());

        // Assert
        Assert.False(registeredSubject.IsRegistered);
    }
}
