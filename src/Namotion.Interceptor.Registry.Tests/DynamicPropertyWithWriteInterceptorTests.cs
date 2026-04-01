using System.ComponentModel.DataAnnotations;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Validation;

namespace Namotion.Interceptor.Registry.Tests;

/// <summary>
/// Tests that AddProperty works correctly when a validator accesses the registry
/// during the initial SetPropertyValueWithInterception, and under concurrent access.
/// </summary>
public class DynamicPropertyWithWriteInterceptorTests
{
    [Fact]
    public void WhenLifecycleHandlerAddsDynamicPropertyAndValidatorAccessesRegistry_ThenShouldNotThrow()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithPropertyValidation()
            .WithService<IPropertyValidator>(() => new RegistryAccessingValidator())
            .WithService<ILifecycleHandler>(
                () => new DynamicPropertyAddingLifecycleHandler(),
                handler => handler is DynamicPropertyAddingLifecycleHandler);

        var root = new Person(context) { FirstName = "Root" };

        // Act & Assert — setting Father triggers lifecycle for the child,
        // which adds a dynamic property; the validator should not throw.
        var child = new Person { FirstName = "Child" };
        root.Father = child;
    }

    [Fact]
    public async Task WhenAddPropertyCalledConcurrently_ThenAllPropertiesAreRegistered()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new Person(context) { FirstName = "Root" };
        var registeredSubject = root.TryGetRegisteredSubject()!;

        var propertyCount = 50;
        var barrier = new Barrier(propertyCount);

        // Act — add many properties concurrently
        var tasks = Enumerable.Range(0, propertyCount).Select(i => Task.Run(() =>
        {
            barrier.SignalAndWait();
            registeredSubject.AddProperty($"Dynamic_{i}", typeof(string), _ => $"value_{i}", null);
        })).ToArray();

        await Task.WhenAll(tasks);

        // Assert — all properties should be present
        for (var i = 0; i < propertyCount; i++)
        {
            Assert.NotNull(registeredSubject.TryGetProperty($"Dynamic_{i}"));
        }
    }

    [Fact]
    public void WhenAddPropertyTriggersAddAttribute_ThenRecursiveAddPropertySucceeds()
    {
        // Arrange — AddAttribute internally calls Parent.AddProperty,
        // so AddProperty is re-entered on the same thread.
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new Person(context) { FirstName = "Root" };
        var registeredSubject = root.TryGetRegisteredSubject()!;

        // Act — add a property, then add an attribute to it (recursive AddProperty)
        var property = registeredSubject.AddProperty("Dynamic", typeof(string), _ => "value", null);
        property.AddAttribute<string>("MyAttr", _ => "attr-value");

        // Assert — both the property and its attribute should be registered
        Assert.NotNull(registeredSubject.TryGetProperty("Dynamic"));
        Assert.NotNull(registeredSubject.TryGetProperty("Dynamic@MyAttr"));
    }

    /// <summary>
    /// Validator that calls GetRegisteredProperty() on every property write,
    /// simulating EnumerationValueValidator.
    /// </summary>
    private class RegistryAccessingValidator : IPropertyValidator
    {
        public IEnumerable<ValidationResult> Validate<TProperty>(PropertyReference property, TProperty value)
        {
            var registeredProperty = property.GetRegisteredProperty();
            yield break;
        }
    }

    /// <summary>
    /// Lifecycle handler that adds a dynamic property on context attach,
    /// simulating VariableObjectMetadataPropertyInitializer.
    /// </summary>
    private class DynamicPropertyAddingLifecycleHandler : ILifecycleHandler
    {
        private const string MetadataPropertyName = "__metadata";

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.IsContextAttach)
            {
                var registeredSubject = change.Subject.TryGetRegisteredSubject();
                if (registeredSubject is null)
                {
                    return;
                }

                if (registeredSubject.TryGetProperty(MetadataPropertyName) is null)
                {
                    var metadata = "test-metadata";
                    registeredSubject.AddProperty(MetadataPropertyName, typeof(string), _ => metadata, null);
                }
            }
        }
    }
}
