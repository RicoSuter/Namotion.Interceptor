using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Registry.Tests;

public class AttributeTests
{
    [Fact]
    public void WhenAddingAnAttribute_ThenValueCanBeReadAndWritten()
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

        var attributeValue = 42;

        var registeredProperty = person.TryGetRegisteredProperty(p => p!.FirstName)!;

        var attributePropertyName = registeredProperty.AddAttribute("MyAttribute",
            typeof(int), _ => attributeValue, (_, v) => attributeValue = (int)v!);

        attributePropertyName.AddAttribute("MyAttribute2",
            typeof(int), _ => attributeValue, (_, v) => attributeValue = (int)v!);

        var attribute = registeredProperty.TryGetAttribute("MyAttribute");
        var attribute2 = attribute?.TryGetAttribute("MyAttribute2");

        // Assert
        Assert.NotNull(attribute);
        Assert.NotNull(attribute2);

        attribute.SetValue(500);
        var newValue = attribute2.GetValue();
        Assert.Equal(500, newValue);
    }

    [Fact]
    public void WhenRegisteringDynamicDerivedProperty_ThanChangeTrackingIsWorking()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyChangedObservable()
            .WithDerivedPropertyChangeDetection()
            .WithRegistry();

        var person = new Person(context)
        {
            FirstName = "John"
        };

        var changes = new List<SubjectPropertyChange>();

        // Act
        var dynamicDerivedProperty = person
            .TryGetRegisteredSubject()!
            .AddDerivedProperty("DynamicDerivedProperty", typeof(string), _ => "Mr. " + person.FirstName, null);

        context
            .GetPropertyChangedObservable(ImmediateScheduler.Instance)
            .Where(c => c.Property == dynamicDerivedProperty)
            .Subscribe(a => changes.Add(a));

        person.FirstName = "Rico";

        // Assert
        Assert.Contains(changes, x => x.NewValue!.Equals("Mr. Rico"));
    }

    [Fact]
    public void WhenRegisteringDynamicDerivedAttribute_ThanChangeTrackingIsWorking()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyChangedObservable()
            .WithDerivedPropertyChangeDetection()
            .WithRegistry();

        var person = new Person(context)
        {
            FirstName = "John"
        };

        var changes = new List<SubjectPropertyChange>();

        // Act
        var dynamicDerivedAttribute = person
            .TryGetRegisteredSubject()!
            .TryGetProperty(nameof(Person.FirstName))!
            .AddDerivedAttribute("DynamicDerivedAttribute", typeof(string), _ => "Mr. " + person.FirstName, null);

        context
            .GetPropertyChangedObservable(ImmediateScheduler.Instance)
            .Where(c => c.Property == dynamicDerivedAttribute)
            .Subscribe(a => changes.Add(a));

        person.FirstName = "Rico";

        // Assert
        Assert.Contains(changes, x => x.NewValue!.Equals("Mr. Rico"));
    }

    [Fact]
    public void WhenRegisteringDynamicProperty_ThanChangeTrackingIsWorking()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyChangedObservable()
            .WithDerivedPropertyChangeDetection()
            .WithRegistry();

        var person = new Person(context)
        {
            FirstName = "John"
        };

        var value = "Test";

        var changes = new List<SubjectPropertyChange>();

        // Act
        var dynamicProperty = person
            .TryGetRegisteredSubject()!
            .AddProperty("DynamicProperty", typeof(string), _ => value, (_, v) => value = (string)v!);

        context
            .GetPropertyChangedObservable(ImmediateScheduler.Instance)
            .Where(c => c.Property == dynamicProperty)
            .Subscribe(a => changes.Add(a));

        dynamicProperty.SetValue("Abc");

        // Assert
        Assert.Contains(changes, x => x.NewValue!.Equals("Abc"));
    }

    [Fact]
    public void WhenRegisteringDynamicAttribute_ThanChangeTrackingIsWorking()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyChangedObservable()
            .WithDerivedPropertyChangeDetection()
            .WithRegistry();

        var person = new Person(context)
        {
            FirstName = "John"
        };

        var value = "Test";
        var changes = new List<SubjectPropertyChange>();

        // Act
        var dynamicAttribute = person
            .TryGetRegisteredSubject()!
            .TryGetProperty(nameof(Person.FirstName))!
            .AddAttribute("DynamicAttribute", typeof(string), _ => value, (_, v) => value = (string)v!);

        context
            .GetPropertyChangedObservable(ImmediateScheduler.Instance)
            .Where(c => c.Property == dynamicAttribute)
            .Subscribe(a => changes.Add(a));

        dynamicAttribute.SetValue("Abc");

        // Assert
        Assert.Contains(changes, x => x.NewValue!.Equals("Abc"));
    }
}