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
            _ => attributeValue, (_, v) => attributeValue = v!);

        attributePropertyName.AddAttribute("MyAttribute2",
            _ => attributeValue, (_, v) => attributeValue = v!);

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
            .WithPropertyChangeObservable()
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
            .AddDerivedProperty<string>("DynamicDerivedProperty", _ => "Mr. " + person.FirstName);

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(c => c.Property == dynamicDerivedProperty)
            .Subscribe(a => changes.Add(a));

        person.FirstName = "Rico";

        // Assert
        Assert.Contains(changes, x => x.GetNewValue<string>().Equals("Mr. Rico"));
    }

    [Fact]
    public void WhenRegisteringDynamicDerivedAttribute_ThanChangeTrackingIsWorking()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyChangeObservable()
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
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(c => c.Property == dynamicDerivedAttribute)
            .Subscribe(a => changes.Add(a));

        person.FirstName = "Rico";

        // Assert
        Assert.Contains(changes, x => x.GetNewValue<string>().Equals("Mr. Rico"));
    }

    [Fact]
    public void WhenRegisteringDynamicProperty_ThanChangeTrackingIsWorking()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyChangeObservable()
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
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(c => c.Property == dynamicProperty)
            .Subscribe(a => changes.Add(a));

        dynamicProperty.SetValue("Abc");

        // Assert
        Assert.Contains(changes, x => x.GetNewValue<string>().Equals("Abc"));
    }

    [Fact]
    public void WhenRegisteringDynamicAttribute_ThanChangeTrackingIsWorking()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyChangeObservable()
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
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(c => c.Property == dynamicAttribute)
            .Subscribe(a => changes.Add(a));

        dynamicAttribute.SetValue("Abc");

        // Assert
        Assert.Contains(changes, x => x.GetNewValue<string>().Equals("Abc"));
    }

    [Fact]
    public void WhenChangingDerivedAttributeViaSetValue_ThenChangeIsTriggered()
    {
        // Arrange - simulating VariableMethodAttribute pattern
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var person = new Person(context)
        {
            FirstName = "Rico"
        };

        // Add a derived attribute (like VariableMethodAttribute.InitializeProperty does)
        var isExecuting = false;
        var firstNameProperty = person.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.FirstName))!;
        var derivedAttribute = firstNameProperty.AddDerivedAttribute(
            "IsExecuting",
            typeof(bool),
            _ => isExecuting,
            (_, v) => isExecuting = v?.Equals(true) == true);

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        changes.Clear();

        // Act - Change via SetValue (like: property.TryGetAttribute("IsExecuting")?.SetValue(true))
        derivedAttribute.SetValue(true);

        // Assert
        Assert.Contains(changes, c =>
            c.Property.Name == $"{nameof(Person.FirstName)}@IsExecuting" &&
            c.GetNewValue<bool>() == true);
    }

    [Fact]
    public void WhenChangingDerivedAttributeViaTryGetAttribute_ThenChangeIsTriggered()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var person = new Person(context)
        {
            FirstName = "Rico"
        };

        // Add a derived attribute
        var isExecuting = false;
        var firstNameProperty = person.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.FirstName))!;
        firstNameProperty.AddDerivedAttribute(
            "IsExecuting",
            typeof(bool),
            _ => isExecuting,
            (_, v) => isExecuting = v?.Equals(true) == true);

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        changes.Clear();

        // Act - Change via TryGetAttribute (the exact pattern from VariableMethodAttribute)
        firstNameProperty.TryGetAttribute("IsExecuting")?.SetValue(true);

        // Assert
        Assert.Contains(changes, c =>
            c.Property.Name == $"{nameof(Person.FirstName)}@IsExecuting" &&
            c.GetNewValue<bool>() == true);
    }

    [Fact]
    public void WhenDerivedPropertyWithSetterUsesShortCircuit_ThenDependenciesAreRerecordedOnSetValue()
    {
        // This tests that when SetValue is called on a derived property with a setter,
        // dependencies are re-recorded based on the new internal state.
        //
        // Scenario: ComputedFlag = localFlag || SourceFlag
        // 1. localFlag=true causes short-circuit (SourceFlag not read, dependency lost)
        // 2. SetValue(false) sets localFlag=false, now SourceFlag IS read
        // 3. Dependencies are re-recorded, so changing SourceFlag triggers recalculation

        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var person = new Person(context)
        {
            FirstName = "Rico"
        };

        var firstNameProperty = person.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.FirstName))!;

        // Add SourceFlag attribute
        var sourceFlag = true;
        firstNameProperty.AddAttribute(
            "SourceFlag",
            typeof(bool),
            _ => sourceFlag,
            (_, v) => sourceFlag = v?.Equals(true) == true);

        // Add ComputedFlag with short-circuit: localFlag || SourceFlag
        // Initial state: localFlag=true, so SourceFlag is NOT read (short-circuit)
        var localFlag = true;
        var computedAttribute = firstNameProperty.AddDerivedAttribute(
            "ComputedFlag",
            typeof(bool),
            _ => localFlag || firstNameProperty.TryGetAttribute("SourceFlag")?.GetValue() is true,
            (_, v) => localFlag = v?.Equals(true) == true);

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(c => c.Property.Name == $"{nameof(Person.FirstName)}@ComputedFlag")
            .Subscribe(changes.Add);

        changes.Clear();

        // Act: SetValue(false) → localFlag=false
        // Now SourceFlag IS read (no short-circuit), dependency should be recorded
        computedAttribute.SetValue(false);
        Assert.True((bool)computedAttribute.GetValue()!); // Still true because SourceFlag=true
        changes.Clear();

        // Change SourceFlag to false - should trigger ComputedFlag recalculation
        firstNameProperty.TryGetAttribute("SourceFlag")?.SetValue(false);

        // Assert: ComputedFlag should be false and change event should fire
        Assert.False((bool)computedAttribute.GetValue()!);
        Assert.Contains(changes, c => c.GetNewValue<bool>() == false);
    }
}