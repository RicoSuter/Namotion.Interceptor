using System.Reactive.Linq;
using Namotion.Interceptor.Attributes;
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
        var attribute3 = person.GetRegisteredAttribute(nameof(Person.FirstName), "MyAttribute");
        var attribute4 = new PropertyReference(person, nameof(Person.FirstName))
            .TryGetRegisteredAttribute("MyAttribute");

        // Assert
        Assert.NotNull(attribute);
        Assert.NotNull(attribute2);
        Assert.NotNull(attribute3);
        Assert.NotNull(attribute4);

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

        // Act
        var person = new Person(context)
        {
            FirstName = "John"
        };

        var changes = new List<SubjectPropertyChange>();
        var dynamicDerivedProperty = person
            .TryGetRegisteredSubject()!
            .AddProperty("DynamicDerivedProperty", typeof(string), _ => "Mr. " + person.FirstName, null, new DerivedAttribute());

        context
            .GetPropertyChangedObservable()
            .Where(c => c.Property == dynamicDerivedProperty.Property)
            .Subscribe(a => changes.Add(a));
        
        person.FirstName = "Rico";

        // Assert
        Assert.Contains(changes, x => x.NewValue!.Equals("Mr. Rico"));
    }
}