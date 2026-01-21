using Namotion.Interceptor.Generator.Tests.Models;
using Namotion.Interceptor.Interceptors;

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
}
