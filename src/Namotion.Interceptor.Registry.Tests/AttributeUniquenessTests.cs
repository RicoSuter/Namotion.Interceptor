using System.Reactive.Concurrency;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

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
        var registeredPropertyCount = firstName.Parent.Properties.Length;

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
            firstName.AddDerivedAttribute("MaxLength", typeof(int), _ => 1, null));

        Assert.Equal(
            "The property 'FirstName' of subject 'Person' already has an attribute named 'MaxLength' (property 'FirstName_MaxLength').",
            exception.Message);
        Assert.Equal(subjectPropertyCount, ((IInterceptorSubject)person).Properties.Count);
        Assert.Equal(registeredPropertyCount, firstName.Parent.Properties.Length);
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

    [Fact]
    public void WhenThreadsAddDifferentPropertiesForTheSameAttributePairConcurrently_ThenExactlyOneSucceeds()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);
        var registered = person.TryGetRegisteredSubject()!;

        const int threadCount = 8;
        var barrier = new Barrier(threadCount);
        var successes = 0;
        var failures = 0;

        // Act
        var threads = new Thread[threadCount];
        for (var index = 0; index < threadCount; index++)
        {
            var slot = index;
            threads[index] = new Thread(() =>
            {
                barrier.SignalAndWait();
                try
                {
                    registered.AddProperty($"Flag{slot}", typeof(int), _ => 1, null,
                        new PropertyAttributeAttribute(nameof(Person.FirstName), "Flag"));
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
        Assert.Equal(1, registered.Properties.Count(p => p.IsAttribute && p.AttributeMetadata.AttributeName == "Flag"));
        Assert.Equal(1, ((IInterceptorSubject)person).Properties.Keys.Count(key => key.StartsWith("Flag", StringComparison.Ordinal)));
    }

    [Fact]
    public void WhenASubjectDeclaresTwoPropertiesForOneAttributePair_ThenThePropertyWhoseNameSortsFirstProvidesTheAttribute()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();

        // Act
        var subject = new DuplicateAttributeTarget(context);

        // Assert
        var registered = subject.TryGetRegisteredSubject()!;
        Assert.Equal("Forward_Enabled", registered.TryGetPropertyAttribute("Forward", "IsEnabled")!.Name);

        var loser = registered.TryGetProperty("Forward_IsEnabled")!;
        Assert.False(loser.IsAttribute);
        Assert.Equal("Forward_IsEnabled", loser.BrowseName);

        var attribute = Assert.Single(registered.TryGetProperty("Forward")!.Attributes);
        Assert.Equal("Forward_Enabled", attribute.Name);
    }

    [Fact]
    public void WhenADuplicateAttributeTargetIsAttachedAndDetached_ThenItIsRegisteredAndReleased()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var registry = context.GetService<ISubjectRegistry>();
        var parent = new DuplicateAttributeTarget(context);
        var child = new DuplicateAttributeTarget();

        // Act
        parent.Child = child;

        // Assert
        Assert.NotNull(child.TryGetRegisteredSubject());
        Assert.Contains(child, registry.KnownSubjects.Keys);

        // Act
        parent.Child = null;

        // Assert
        Assert.DoesNotContain(child, registry.KnownSubjects.Keys);
    }

    [Fact]
    public void WhenGetOrAddAttributeFindsAnExistingAttribute_ThenNoChangeIsPublished()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions().WithRegistry();
        var person = new Person(context);
        var firstName = person.TryGetRegisteredProperty(p => p!.FirstName)!;
        var first = firstName.AddAttribute("Flag", typeof(int), _ => 1, null);
        var subjectPropertyCount = ((IInterceptorSubject)person).Properties.Count;

        var changes = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change => changes.Add(change));

        // Act
        var second = firstName.GetOrAddAttribute("Flag", typeof(int), _ => 2, null);

        // Assert
        Assert.Same(first, second);
        Assert.Empty(changes);
        Assert.Equal(subjectPropertyCount, ((IInterceptorSubject)person).Properties.Count);
    }

    [Fact]
    public void WhenGetOrAddAttributeFindsAStaticAttributeProperty_ThenItReturnsThatProperty()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);
        var firstName = person.TryGetRegisteredProperty(p => p!.FirstName)!;
        var existing = person.TryGetRegisteredProperty(p => p!.FirstName_MaxLength)!;
        var subjectPropertyCount = ((IInterceptorSubject)person).Properties.Count;

        // Act
        var attribute = firstName.GetOrAddAttribute("MaxLength", typeof(int), _ => 0, null);

        // Assert
        Assert.Same(existing, attribute);
        Assert.Equal(123, attribute.GetValue());
        Assert.Equal(subjectPropertyCount, ((IInterceptorSubject)person).Properties.Count);
    }

    [Fact]
    public void WhenGetOrAddAttributeFindsAnAttributeOfAnotherType_ThenItThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);
        var firstName = person.TryGetRegisteredProperty(p => p!.FirstName)!;
        var subjectPropertyCount = ((IInterceptorSubject)person).Properties.Count;
        var registeredPropertyCount = firstName.Parent.Properties.Length;

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
            firstName.GetOrAddAttribute("MaxLength", typeof(string), _ => "x", null));

        Assert.Equal(
            "The property 'FirstName' of subject 'Person' already has an attribute named 'MaxLength' of type 'System.Int32', not 'System.String' (property 'FirstName_MaxLength').",
            exception.Message);
        Assert.Equal(subjectPropertyCount, ((IInterceptorSubject)person).Properties.Count);
        Assert.Equal(registeredPropertyCount, firstName.Parent.Properties.Length);
    }

    [Fact]
    public void WhenGetOrAddAttributeFindsADynamicAttribute_ThenItReturnsThatAttribute()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);
        var firstName = person.TryGetRegisteredProperty(p => p!.FirstName)!;
        var first = firstName.AddAttribute("Flag", typeof(string), _ => "first", null);

        // Act
        var attribute = firstName.GetOrAddAttribute("Flag", typeof(string), _ => "second", null);

        // Assert
        Assert.Same(first, attribute);
        Assert.Equal("first", attribute.GetValue());
    }

    [Fact]
    public void WhenGetOrAddAttributeFindsNoAttribute_ThenItAddsOne()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);
        var firstName = person.TryGetRegisteredProperty(p => p!.FirstName)!;

        // Act
        var attribute = firstName.GetOrAddAttribute("Flag", typeof(string), _ => "value", null);

        // Assert
        Assert.True(attribute.IsAttribute);
        Assert.Equal("Flag", attribute.BrowseName);
        Assert.Same(attribute, firstName.TryGetAttribute("Flag"));
        Assert.Equal("value", attribute.GetValue());
        Assert.True(((IInterceptorSubject)person).Properties.ContainsKey("FirstName@Flag"));
    }

    [Fact]
    public void WhenGetOrAddDerivedAttributeFindsAStaticAttributeProperty_ThenItReturnsThatProperty()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);
        var firstName = person.TryGetRegisteredProperty(p => p!.FirstName)!;
        var existing = person.TryGetRegisteredProperty(p => p!.FirstName_MaxLength)!;

        // Act
        var attribute = firstName.GetOrAddDerivedAttribute("MaxLength", typeof(int), _ => 1, null);

        // Assert
        Assert.Same(existing, attribute);
    }

    [Fact]
    public void WhenGetOrAddDerivedAttributeFindsADynamicAttribute_ThenItReturnsThatAttribute()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);
        var firstName = person.TryGetRegisteredProperty(p => p!.FirstName)!;
        var first = firstName.AddAttribute("Flag", typeof(string), _ => "first", null);

        // Act
        var attribute = firstName.GetOrAddDerivedAttribute("Flag", typeof(string), _ => "second", null);

        // Assert
        Assert.Same(first, attribute);
    }

    [Fact]
    public void WhenGetOrAddDerivedAttributeFindsNoAttribute_ThenItAddsADerivedOne()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context) { FirstName = "Ann" };
        var firstName = person.TryGetRegisteredProperty(p => p!.FirstName)!;

        // Act
        var attribute = firstName.GetOrAddDerivedAttribute("Greeting", typeof(string), _ => "Hi " + person.FirstName, null);

        // Assert
        Assert.Same(attribute, firstName.TryGetAttribute("Greeting"));
        Assert.Contains(attribute.ReflectionAttributes, reflectionAttribute => reflectionAttribute is DerivedAttribute);
        Assert.Equal("Hi Ann", attribute.GetValue());
    }

    [Fact]
    public void WhenThreadsGetOrAddTheSameAttributeConcurrently_ThenAllReceiveTheSameInstance()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);
        var firstName = person.TryGetRegisteredProperty(p => p!.FirstName)!;

        const int threadCount = 8;
        var barrier = new Barrier(threadCount);
        var results = new RegisteredSubjectProperty[threadCount];

        // Act
        var threads = new Thread[threadCount];
        for (var index = 0; index < threadCount; index++)
        {
            var slot = index;
            threads[index] = new Thread(() =>
            {
                barrier.SignalAndWait();
                results[slot] = firstName.GetOrAddAttribute("Flag", typeof(int), _ => slot, null);
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
        Assert.All(results, result => Assert.Same(results[0], result));
        Assert.Equal(1, firstName.Parent.Properties.Count(p => p.Name == "FirstName@Flag"));
    }

    [Fact]
    public void WhenAnInitializerUsesGetOrAddAttribute_ThenReRegistrationKeepsOneAttribute()
    {
        // Arrange
        var initializer = new FlagInitializer(useGetOrAdd: true);
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService<ISubjectPropertyInitializer>(() => initializer);

        var parent = new Person(context);
        var child = new Person();

        // Act: attach, detach and attach again, which runs the initializer twice for the child's properties.
        parent.Mother = child;
        parent.Mother = null;
        parent.Mother = child;

        // Assert
        var firstName = child.TryGetRegisteredProperty(p => p!.FirstName)!;
        Assert.NotNull(firstName.TryGetAttribute("Flag"));
        Assert.Equal(1, firstName.Parent.Properties.Count(p => p.Name == "FirstName@Flag"));
    }

    [Fact]
    public void WhenAnInitializerUsesAddAttribute_ThenReRegistrationThrows()
    {
        // Arrange
        var initializer = new FlagInitializer(useGetOrAdd: false);
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService<ISubjectPropertyInitializer>(() => initializer);

        var parent = new Person(context);
        var child = new Person();
        parent.Mother = child;
        parent.Mother = null;

        // Act & Assert: the dynamic attribute survives on the subject, so the second registration finds it.
        var exception = Assert.Throws<InvalidOperationException>(() => parent.Mother = child);
        Assert.Equal(
            "The property 'FirstName' of subject 'Person' already has an attribute named 'Flag' (property 'FirstName@Flag').",
            exception.Message);
    }

    private sealed class FlagInitializer(bool useGetOrAdd) : ISubjectPropertyInitializer
    {
        public void InitializeProperty(RegisteredSubjectProperty property)
        {
            if (property.IsAttribute || property.Name != nameof(Person.FirstName))
            {
                return;
            }

            if (useGetOrAdd)
            {
                property.GetOrAddAttribute("Flag", typeof(bool), _ => true, null);
            }
            else
            {
                property.AddAttribute("Flag", typeof(bool), _ => true, null);
            }
        }
    }
}
