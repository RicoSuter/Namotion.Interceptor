using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Registry.Tests.Models;

namespace Namotion.Interceptor.Registry.Tests;

public class AttributeUniquenessTests
{
    [Fact]
    public void WhenAStaticAttributePropertyExists_ThenAddAttributeWithTheSameNameThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);
        var firstName = person.TryGetRegisteredProperty(p => p!.FirstName)!;
        var subjectPropertyCount = ((IInterceptorSubject)person).Properties.Count;
        var registeredPropertyCount = firstName.Parent.Properties.Length;

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
            firstName.AddAttribute("MaxLength", typeof(int), _ => 1, null));

        Assert.Equal(
            "The property 'FirstName' of subject 'Person' already has an attribute named 'MaxLength' (property 'FirstName_MaxLength').",
            exception.Message);
        Assert.Equal(subjectPropertyCount, ((IInterceptorSubject)person).Properties.Count);
        Assert.Equal(registeredPropertyCount, firstName.Parent.Properties.Length);
    }

    [Fact]
    public void WhenAStaticAttributePropertyExists_ThenAddDerivedAttributeWithTheSameNameThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);
        var firstName = person.TryGetRegisteredProperty(p => p!.FirstName)!;
        var subjectPropertyCount = ((IInterceptorSubject)person).Properties.Count;

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
            firstName.AddDerivedAttribute("MaxLength", typeof(int), _ => 1, null));

        Assert.Equal(
            "The property 'FirstName' of subject 'Person' already has an attribute named 'MaxLength' (property 'FirstName_MaxLength').",
            exception.Message);
        Assert.Equal(subjectPropertyCount, ((IInterceptorSubject)person).Properties.Count);
    }

    [Fact]
    public void WhenTheSameDynamicAttributeIsAddedTwice_ThenTheSecondCallThrowsAndTheFirstStays()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);
        var firstName = person.TryGetRegisteredProperty(p => p!.FirstName)!;
        var first = firstName.AddAttribute("Flag", typeof(string), _ => "first", null);

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
            firstName.AddAttribute("Flag", typeof(string), _ => "second", null));

        Assert.Equal(
            "The property 'FirstName' of subject 'Person' already has an attribute named 'Flag' (property 'FirstName@Flag').",
            exception.Message);
        Assert.Same(first, firstName.TryGetAttribute("Flag"));
        Assert.Equal("first", first.GetValue());
        Assert.Equal("first", ((IInterceptorSubject)person).Properties["FirstName@Flag"].GetValue?.Invoke(person));
    }

    [Fact]
    public void WhenAPropertyNameIsTaken_ThenAddPropertyThrowsBeforeTheSubjectChanges()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);
        var registered = person.TryGetRegisteredSubject()!;
        var subject = (IInterceptorSubject)person;
        var before = subject.Properties;

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
            registered.AddProperty("FirstName", typeof(string), _ => "x", null));

        Assert.Equal("The subject 'Person' already has a property named 'FirstName'.", exception.Message);
        Assert.Same(before, subject.Properties);
    }

    [Fact]
    public void WhenAddPropertyPassesAPropertyAttributeForAnExistingPair_ThenItThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);
        var registered = person.TryGetRegisteredSubject()!;
        var subject = (IInterceptorSubject)person;
        var before = subject.Properties;

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
            registered.AddProperty("AnotherMaxLength", typeof(int), _ => 1, null,
                new PropertyAttributeAttribute(nameof(Person.FirstName), "MaxLength")));

        Assert.Equal(
            "The property 'FirstName' of subject 'Person' already has an attribute named 'MaxLength' (property 'FirstName_MaxLength').",
            exception.Message);
        Assert.Same(before, subject.Properties);
    }

    [Fact]
    public void WhenThreadsAddTheSameAttributeConcurrently_ThenExactlyOneSucceeds()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);
        var firstName = person.TryGetRegisteredProperty(p => p!.FirstName)!;

        const int threadCount = 8;
        var barrier = new Barrier(threadCount);
        var successes = 0;
        var failures = 0;

        // Act
        var threads = new Thread[threadCount];
        for (var index = 0; index < threadCount; index++)
        {
            threads[index] = new Thread(() =>
            {
                barrier.SignalAndWait();
                try
                {
                    firstName.AddAttribute("Flag", typeof(int), _ => 1, null);
                    Interlocked.Increment(ref successes);
                }
                catch (InvalidOperationException)
                {
                    Interlocked.Increment(ref failures);
                }
            })
            {
                IsBackground = true
            };
            threads[index].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        // Assert
        Assert.Equal(1, successes);
        Assert.Equal(threadCount - 1, failures);
        Assert.Equal(1, firstName.Parent.Properties.Count(p => p.Name == "FirstName@Flag"));
        Assert.True(((IInterceptorSubject)person).Properties.ContainsKey("FirstName@Flag"));
    }
}
