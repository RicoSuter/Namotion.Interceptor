using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry.Tests;

public class RegisteredSubjectAttachmentTests
{
    [Fact]
    public void WhenSubjectsAreAttached_ThenTheirInstancesAreAttached()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();

        // Act
        var person = new Person(context) { Mother = new Person() };

        // Assert
        Assert.True(person.TryGetRegisteredSubject()!.IsAttached);
        Assert.Empty(person.TryGetRegisteredSubject()!.Parents);
        Assert.True(person.Mother.TryGetRegisteredSubject()!.IsAttached);
    }

    [Fact]
    public void WhenSubjectIsDetached_ThenItsInstanceAndThoseOfTheSubjectsBelowItAreNoLongerAttached()
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
        Assert.False(registeredMother.IsAttached);
        Assert.False(registeredGrandmother.IsAttached);
        Assert.Empty(registeredMother.Parents);
        Assert.True(person.TryGetRegisteredSubject()!.IsAttached);
    }

    [Fact]
    public void WhenSubjectIsAttachedAgain_ThenItIsRegisteredAsANewInstanceAndTheOldOneStaysDetached()
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
        Assert.True(secondRegistration.IsAttached);
        Assert.False(firstRegistration.IsAttached);
    }

    [Fact]
    public void WhenASubjectHeldByTwoPropertiesLosesOne_ThenItStaysAttachedAsTheSameInstance()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var parent = new Person();
        var person = new Person(context) { Mother = parent, Father = parent };
        var registeredParent = parent.TryGetRegisteredSubject()!;

        // Act
        person.Father = null;

        // Assert
        Assert.Same(registeredParent, parent.TryGetRegisteredSubject());
        Assert.True(registeredParent.IsAttached);
        Assert.Single(registeredParent.Parents);
    }

    [Fact]
    public void WhenTheSameSubjectIsAssignedAgain_ThenItStaysAttachedAsTheSameInstance()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var mother = new Person();
        var person = new Person(context) { Mother = mother };
        var registeredMother = mother.TryGetRegisteredSubject()!;

        // Act
        person.Mother = mother;

        // Assert
        Assert.Same(registeredMother, mother.TryGetRegisteredSubject());
        Assert.True(registeredMother.IsAttached);
        Assert.Single(registeredMother.Parents);
    }

    [Fact]
    public void WhenADictionaryEntryIsReKeyed_ThenTheSubjectStaysAttachedAsTheSameInstance()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var light = new Light { Name = "Lamp" };
        var group = new LightGroup(context) { LightsByName = new Dictionary<string, Light> { ["A"] = light } };
        var registeredLight = light.TryGetRegisteredSubject()!;

        // Act
        group.LightsByName = new Dictionary<string, Light> { ["B"] = light };

        // Assert
        Assert.Same(registeredLight, light.TryGetRegisteredSubject());
        Assert.True(registeredLight.IsAttached);
        Assert.NotEmpty(registeredLight.Parents);
    }

    [Fact]
    public void WhenACollectionItemIsRemoved_ThenOnlyItIsNoLongerAttached()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var first = new Light { Name = "First" };
        var second = new Light { Name = "Second" };
        var group = new LightGroup(context) { Lights = [first, second] };
        var registeredFirst = first.TryGetRegisteredSubject()!;
        var registeredSecond = second.TryGetRegisteredSubject()!;

        // Act
        group.Lights = [second];

        // Assert
        Assert.False(registeredFirst.IsAttached);
        Assert.True(registeredSecond.IsAttached);
        Assert.Single(registeredSecond.Parents);
    }

    [Fact]
    public void WhenASubjectHeldByAnAttributeIsDetached_ThenItIsNoLongerAttached()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var probe = new Person();
        var holder = new AttributeHolder(context) { Name_Probe = probe };
        var registeredProbe = probe.TryGetRegisteredSubject()!;
        Assert.True(registeredProbe.IsAttached);
        Assert.True(Assert.Single(registeredProbe.Parents).Property.IsAttribute);

        // Act
        holder.Name_Probe = null;

        // Assert
        Assert.False(registeredProbe.IsAttached);
    }

    [Fact]
    public void WhenRegisteredSubjectIsCreatedOutsideTheRegistry_ThenItIsNotAttached()
    {
        // Act
        var registeredSubject = new RegisteredSubject(new Person());

        // Assert
        Assert.False(registeredSubject.IsAttached);
    }

    [Fact]
    public void WhenASubtreeIsDetached_ThenNoSubjectBelowTheRootIsEverAttachedWithoutParents()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var grandmother = new Person();
        var sibling = new Person();
        var mother = new Person { Mother = grandmother, Father = sibling, Children = [sibling, new Person()] };
        var person = new Person(context) { Mother = mother };
        RegisteredSubject[] subtree =
        [
            mother.TryGetRegisteredSubject()!, grandmother.TryGetRegisteredSubject()!,
            sibling.TryGetRegisteredSubject()!, mother.Children[1].TryGetRegisteredSubject()!
        ];
        var violations = new List<string>();
        var checks = 0;
        void Check()
        {
            foreach (var registeredSubject in subtree)
            {
                checks++;
                if (registeredSubject.Parents.Length == 0 && registeredSubject.IsAttached)
                {
                    violations.Add(registeredSubject.Subject.GetHashCode().ToString());
                }
            }
        }

        context.AddService(new BeforeRegistryProbe(Check));
        context.AddService(new AfterRegistryProbe(Check));

        // Act
        person.Mother = null;

        // Assert
        Assert.Empty(violations);
        Assert.True(checks > 0);
        Assert.All(subtree, registeredSubject => Assert.False(registeredSubject.IsAttached));
    }

    [RunsBefore(typeof(SubjectRegistry))]
    private sealed class BeforeRegistryProbe(Action check) : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change) => check();
    }

    [RunsAfter(typeof(SubjectRegistry))]
    [RunsBefore(typeof(ContextInheritanceHandler))]
    private sealed class AfterRegistryProbe(Action check) : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change) => check();
    }
}

[InterceptorSubject]
public partial class AttributeHolder
{
    public partial string? Name { get; set; }

    [PropertyAttribute(nameof(Name), "Probe")]
    public partial Person? Name_Probe { get; set; }
}
