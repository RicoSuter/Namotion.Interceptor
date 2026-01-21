using Namotion.Interceptor.Generator.Tests.Models;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Generator.Tests;

public class PropertyHooksTests
{
    [Fact]
    public void WhenPropertyIsSet_ThenOnChangingIsCalledBeforeWrite()
    {
        // Arrange
        var person = new PersonWithHooks();

        // Act
        person.FirstName = "Rico";

        // Assert
        Assert.Equal("Rico", person.LastChangingValue);
        Assert.Contains("Changing:Rico", person.HookCalls);
    }

    [Fact]
    public void WhenPropertyIsSet_ThenOnChangedIsCalledAfterWrite()
    {
        // Arrange
        var person = new PersonWithHooks();

        // Act
        person.FirstName = "Rico";

        // Assert
        Assert.Equal("Rico", person.LastChangedValue);
        Assert.Contains("Changed:Rico", person.HookCalls);
    }

    [Fact]
    public void WhenOnChangingModifiesValue_ThenModifiedValueIsWritten()
    {
        // Arrange
        var person = new PersonWithHooks();
        person.ValueToCoerce = "Coerced";

        // Act
        person.FirstName = "Original";

        // Assert
        Assert.Equal("Coerced", person.FirstName);
        Assert.Equal("Coerced", person.LastChangedValue);
    }

    [Fact]
    public void WhenOnChangingCancels_ThenValueIsNotWritten()
    {
        // Arrange
        var person = new PersonWithHooks();
        person.FirstName = "Initial";
        person.HookCalls.Clear();
        person.ShouldCancel = true;

        // Act
        person.FirstName = "NewValue";

        // Assert
        Assert.Equal("Initial", person.FirstName);
        Assert.Contains("Changing:NewValue", person.HookCalls);
        Assert.DoesNotContain("Changed:NewValue", person.HookCalls);
    }

    [Fact]
    public void WhenOnChangingCancels_ThenOnChangedIsNotCalled()
    {
        // Arrange
        var person = new PersonWithHooks();
        person.ShouldCancel = true;

        // Act
        person.FirstName = "Rico";

        // Assert
        Assert.Single(person.HookCalls); // Only Changing, not Changed
        Assert.StartsWith("Changing:", person.HookCalls[0]);
    }

    [Fact]
    public void WhenPropertyIsSet_ThenHooksAreCalledInCorrectOrder()
    {
        // Arrange
        var person = new PersonWithHooks();

        // Act
        person.FirstName = "Rico";

        // Assert
        Assert.Equal(2, person.HookCalls.Count);
        Assert.Equal("Changing:Rico", person.HookCalls[0]);
        Assert.Equal("Changed:Rico", person.HookCalls[1]);
    }

    [Fact]
    public void WhenPropertyChangedSubscribed_ThenEventIsFired()
    {
        // Arrange
        var person = new PersonWithHooks();
        var firedEvents = new List<string>();
        person.PropertyChanged += (s, e) => firedEvents.Add(e.PropertyName!);

        // Act
        person.FirstName = "Rico";

        // Assert
        Assert.Single(firedEvents);
        Assert.Equal("FirstName", firedEvents[0]);
    }

    [Fact]
    public void WhenOnChangingCancels_ThenPropertyChangedIsNotFired()
    {
        // Arrange
        var person = new PersonWithHooks();
        var firedEvents = new List<string>();
        person.PropertyChanged += (s, e) => firedEvents.Add(e.PropertyName!);
        person.ShouldCancel = true;

        // Act
        person.FirstName = "Rico";

        // Assert
        Assert.Empty(firedEvents);
    }

    [Fact]
    public void WhenNoPropertyChangedSubscribers_ThenNoException()
    {
        // Arrange
        var person = new PersonWithHooks();

        // Act & Assert - should not throw
        person.FirstName = "Rico";
        Assert.Equal("Rico", person.FirstName);
    }

    /// <summary>
    /// Test that when an interceptor skips the write, OnChanged and PropertyChanged are not called.
    /// </summary>
    public class SkippingWriteInterceptor : IWriteInterceptor
    {
        public bool ShouldSkip { get; set; }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (!ShouldSkip)
            {
                next(ref context);
            }
            // If ShouldSkip is true, we don't call next(), so the write is skipped
        }
    }

    [Fact]
    public void WhenInterceptorSkipsWrite_ThenOnChangedIsNotCalled()
    {
        // Arrange
        var interceptor = new SkippingWriteInterceptor { ShouldSkip = false };
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => interceptor);

        var person = new PersonWithHooks(context);
        person.FirstName = "Initial";
        person.HookCalls.Clear();

        // Act - now enable skipping
        interceptor.ShouldSkip = true;
        person.FirstName = "NewValue";

        // Assert
        Assert.Equal("Initial", person.FirstName);
        Assert.Contains("Changing:NewValue", person.HookCalls);
        Assert.DoesNotContain("Changed:", string.Join(",", person.HookCalls));
    }

    [Fact]
    public void WhenInterceptorSkipsWrite_ThenPropertyChangedIsNotFired()
    {
        // Arrange
        var interceptor = new SkippingWriteInterceptor { ShouldSkip = true };
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => interceptor);

        var person = new PersonWithHooks(context);
        var firedEvents = new List<string>();
        person.PropertyChanged += (s, e) => firedEvents.Add(e.PropertyName!);

        // Act
        person.FirstName = "Rico";

        // Assert
        Assert.Empty(firedEvents);
    }

    [Fact]
    public void WhenOnChangingThrows_ThenValueIsNotWritten()
    {
        // Arrange
        var person = new PersonWithHooks();
        person.FirstName = "Initial";
        person.HookCalls.Clear();
        person.ShouldThrowInChanging = true;

        var firedEvents = new List<string>();
        person.PropertyChanged += (s, e) => firedEvents.Add(e.PropertyName!);

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => person.FirstName = "NewValue");
        Assert.Equal("Exception in OnChanging", exception.Message);
        Assert.Equal("Initial", person.FirstName);
        Assert.Empty(firedEvents);
    }

    [Fact]
    public void WhenOnChangedThrows_ThenValueIsWrittenButPropertyChangedNotFired()
    {
        // Arrange
        var person = new PersonWithHooks();
        person.ShouldThrowInChanged = true;

        var firedEvents = new List<string>();
        person.PropertyChanged += (s, e) => firedEvents.Add(e.PropertyName!);

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => person.FirstName = "Rico");
        Assert.Equal("Exception in OnChanged", exception.Message);
        Assert.Equal("Rico", person.FirstName); // Value IS written
        Assert.Empty(firedEvents); // PropertyChanged NOT fired
    }

    [Fact]
    public void WhenOnChangingCoercesValue_ThenCoercedValuePassedToOnChangedAndPropertyChanged()
    {
        // Arrange
        var person = new PersonWithHooks();
        person.ValueToCoerce = "Coerced";

        var firedEvents = new List<string>();
        person.PropertyChanged += (s, e) => firedEvents.Add(e.PropertyName!);

        // Act
        person.FirstName = "Original";

        // Assert
        Assert.Equal("Coerced", person.FirstName);
        Assert.Equal("Coerced", person.LastChangedValue); // OnChanged receives coerced value
        Assert.Single(firedEvents);
        Assert.Equal("FirstName", firedEvents[0]); // PropertyChanged fires for the property
    }

    [Fact]
    public void WhenInitPropertySet_ThenHooksAreFired()
    {
        // Arrange & Act
        var person = new PersonWithInit { Id = "123" };

        // Assert
        Assert.Equal(2, person.HookCalls.Count);
        Assert.Equal("Changing:123", person.HookCalls[0]);
        Assert.Equal("Changed:123", person.HookCalls[1]);
        Assert.Equal("123", person.Id);
    }

    [Fact]
    public void WhenMultiplePropertyChangedSubscribers_ThenAllReceiveNotifications()
    {
        // Arrange
        var person = new PersonWithHooks();
        var subscriber1Events = new List<string>();
        var subscriber2Events = new List<string>();
        var subscriber3Events = new List<string>();

        person.PropertyChanged += (s, e) => subscriber1Events.Add(e.PropertyName!);
        person.PropertyChanged += (s, e) => subscriber2Events.Add(e.PropertyName!);
        person.PropertyChanged += (s, e) => subscriber3Events.Add(e.PropertyName!);

        // Act
        person.FirstName = "Rico";

        // Assert
        Assert.Single(subscriber1Events);
        Assert.Single(subscriber2Events);
        Assert.Single(subscriber3Events);
        Assert.Equal("FirstName", subscriber1Events[0]);
        Assert.Equal("FirstName", subscriber2Events[0]);
        Assert.Equal("FirstName", subscriber3Events[0]);
    }

    [Fact]
    public void WhenSameValueSetTwice_WithEqualityCheck_ThenPropertyChangedFiresOnlyOnce()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithEqualityCheck();

        var person = new PersonWithHooks(context);
        var firedEvents = new List<string>();
        person.PropertyChanged += (s, e) => firedEvents.Add(e.PropertyName!);

        // Act
        person.FirstName = "Rico";
        person.FirstName = "Rico"; // Same value again

        // Assert
        Assert.Single(firedEvents); // Should only fire once
        Assert.Equal("FirstName", firedEvents[0]);
    }

    [Fact]
    public void WhenSameValueSetTwice_WithoutEqualityCheck_ThenPropertyChangedFiresTwice()
    {
        // Arrange - no equality check configured
        var person = new PersonWithHooks();
        var firedEvents = new List<string>();
        person.PropertyChanged += (s, e) => firedEvents.Add(e.PropertyName!);

        // Act
        person.FirstName = "Rico";
        person.FirstName = "Rico"; // Same value again

        // Assert
        Assert.Equal(2, firedEvents.Count); // Fires twice without equality check
    }

    [Fact]
    public void WhenOverridePropertySet_ThenDerivedHooksAreCalled()
    {
        // Arrange
        var employee = new EmployeeWithVirtualHooks();

        // Act
        employee.Name = "Rico";

        // Assert - Only derived hooks are called (override replaces base)
        Assert.Equal(2, employee.OverrideHookCalls.Count);
        Assert.Equal("Derived.Changing:Rico", employee.OverrideHookCalls[0]);
        Assert.Equal("Derived.Changed:Rico", employee.OverrideHookCalls[1]);
        Assert.Empty(employee.HookCalls); // Base hooks not called for override
    }

    [Fact]
    public void WhenOverridePropertySet_ThenPropertyChangedIsFired()
    {
        // Arrange
        var employee = new EmployeeWithVirtualHooks();
        var firedEvents = new List<string>();
        employee.PropertyChanged += (s, e) => firedEvents.Add(e.PropertyName!);

        // Act
        employee.Name = "Rico";

        // Assert
        Assert.Single(firedEvents);
        Assert.Equal("Name", firedEvents[0]);
    }

    [Fact]
    public void WhenVirtualPropertySetOnBaseType_ThenBaseHooksAreCalled()
    {
        // Arrange
        var person = new PersonWithVirtualHooks();

        // Act
        person.Name = "Rico";

        // Assert
        Assert.Equal(2, person.HookCalls.Count);
        Assert.Equal("Base.Changing:Rico", person.HookCalls[0]);
        Assert.Equal("Base.Changed:Rico", person.HookCalls[1]);
    }

    [Fact]
    public void WhenDerivedClassAdditionalProperty_ThenHooksAreCalled()
    {
        // Arrange
        var employee = new EmployeeWithVirtualHooks();

        // Act
        employee.Department = "Engineering";

        // Assert
        Assert.Equal(2, employee.OverrideHookCalls.Count);
        Assert.Equal("Department.Changing:Engineering", employee.OverrideHookCalls[0]);
        Assert.Equal("Department.Changed:Engineering", employee.OverrideHookCalls[1]);
    }
}
