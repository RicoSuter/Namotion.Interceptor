using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry.Tests;

/// <summary>
/// Pins how a dynamic property's boxed write enters the interceptor chain: the metadata setter
/// unboxes to the declared type and calls the typed write entry, so interceptors see the same
/// <c>TProperty</c> a generated property of that type would produce. A write whose value is not
/// representable in the declared type (a null into a non-nullable value type, say) must keep the
/// pre-typed behavior and arrive at the stored setter untouched through an object-typed chain.
/// </summary>
public class DynamicPropertyTypedChainTests
{
    private sealed class ChainTypeRecordingInterceptor : IWriteInterceptor
    {
        public readonly List<(Type PropertyType, object? NewValue, object? CurrentValue)> Writes = [];

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            Writes.Add((typeof(TProperty), context.NewValue, context.CurrentValue));
            next(ref context);
        }
    }

    [Fact]
    public void WhenDynamicPropertyDeclaredTypeIsScalar_ThenChainCarriesTheDeclaredTypeNotObject()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var probe = new ChainTypeRecordingInterceptor();
        context.AddService(probe);

        IInterceptorSubject person = new Person();
        person.AttachToContext(context);

        var registeredSubject = new RegisteredSubject(person);
        var storedValue = 0.0;
        registeredSubject.AddProperty(
            "Temperature",
            typeof(double),
            _ => storedValue,
            (_, value) => storedValue = (double)value!);

        // Act
        person.Properties["Temperature"].SetValue!(person, 42.0);

        // Assert: registration publishes the initial value first, as a null-to-value write that no
        // double chain can carry, so the assignment is the second write. That one runs the same
        // double chain a generated double property would, so TProperty is double, not object.
        Assert.Equal(2, probe.Writes.Count);
        var write = probe.Writes[1];
        Assert.Equal(typeof(double), write.PropertyType);
        Assert.Equal(42.0, write.NewValue);
        Assert.Equal(0.0, write.CurrentValue);
        Assert.Equal(42.0, storedValue);
    }

    [Fact]
    public void WhenNullIsWrittenIntoNonNullableValueTypeDynamicProperty_ThenSetterStillReceivesNull()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var probe = new ChainTypeRecordingInterceptor();
        context.AddService(probe);

        IInterceptorSubject person = new Person();
        person.AttachToContext(context);

        var registeredSubject = new RegisteredSubject(person);
        object? storedValue = 42.0;
        registeredSubject.AddProperty(
            "Temperature",
            typeof(double),
            _ => storedValue,
            (_, value) => storedValue = value);

        // Act
        person.Properties["Temperature"].SetValue!(person, null);

        // Assert: null cannot travel a double chain, so the write falls back to the object-typed
        // chain and the stored setter receives the null unchanged, as it always did. The first
        // write is the initial value registration publishes.
        Assert.Equal(2, probe.Writes.Count);
        var write = probe.Writes[1];
        Assert.Equal(typeof(object), write.PropertyType);
        Assert.Null(write.NewValue);
        Assert.Equal(42.0, write.CurrentValue);
        Assert.Null(storedValue);
    }

    [Fact]
    public void WhenStoredDynamicPropertyIsStructural_ThenDeclaredTypedChainUsesTrustedReaderWriterPair()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var probe = new ChainTypeRecordingInterceptor();
        context.AddService<IWriteInterceptor>(probe);
        var parent = new Person(context);
        var registeredSubject = parent.TryGetRegisteredSubject()!;
        Person? storedValue = null;
        var property = registeredSubject.AddProperty(
            "Ward",
            typeof(Person),
            _ => storedValue,
            (_, value) => storedValue = (Person?)value);
        var child = new Person { FirstName = "child" };

        // Act
        property.Reference.Metadata.SetValue!(parent, child);

        // Assert
        var write = Assert.Single(probe.Writes);
        Assert.Equal(typeof(Person), write.PropertyType);
        Assert.Same(child, write.NewValue);
        Assert.Null(write.CurrentValue);
        Assert.Same(child, storedValue);
        Assert.Same(context, child.TryGetContext());
    }

    [Fact]
    public void WhenAttachedStructuralDynamicPropertyHasNoRawReader_ThenItIsRejectedBeforeChainOrSetter()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var probe = new ChainTypeRecordingInterceptor();
        context.AddService<IWriteInterceptor>(probe);
        var parent = new Person(context);
        Person? storedValue = null;
        var property = parent.TryGetRegisteredSubject()!.AddProperty(
            "Ward",
            typeof(Person),
            getValue: null,
            setValue: (_, value) => storedValue = (Person?)value);
        var writesBeforeAssignment = probe.Writes.Count;

        // Act
        var exception = Record.Exception(() => property.Reference.Metadata.SetValue!(parent, new Person()));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(writesBeforeAssignment, probe.Writes.Count);
        Assert.Null(storedValue);
    }

    [Fact]
    public void WhenOuterWriteJournalBecomesStaleDuringDownstreamUnwind_ThenRegistryKeepsNewerProjection()
    {
        // Arrange
        var replacement = new NestedReplacementInterceptor();
        var context = InterceptorSubjectContext.Create().WithRegistry();
        context.AddService<IWriteInterceptor>(replacement);
        var parent = new Person(context);
        var first = new Person { FirstName = "first" };
        var second = new Person { FirstName = "second" };
        replacement.Arm(parent, nameof(Person.Father), () => parent.Father = second);

        // Act
        parent.Father = first;

        // Assert
        Assert.Same(second, parent.Father);
        Assert.Null(first.TryGetContext());
        Assert.Same(context, second.TryGetContext());
        var registeredProperty = parent.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.Father))!;
        var child = Assert.Single(registeredProperty.Children);
        Assert.Same(second, child.Subject);
        Assert.DoesNotContain(first, context.GetService<ISubjectRegistry>().KnownSubjects.Keys);
    }

    private sealed class NestedReplacementInterceptor : IWriteInterceptor
    {
        private IInterceptorSubject? _subject;
        private string? _propertyName;
        private Action? _nestedWrite;

        public void Arm(IInterceptorSubject subject, string propertyName, Action nestedWrite)
        {
            _subject = subject;
            _propertyName = propertyName;
            _nestedWrite = nestedWrite;
        }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            var armed = ReferenceEquals(context.Property.Subject, _subject) && context.Property.Name == _propertyName;
            if (armed)
            {
                _subject = null;
            }

            next(ref context);
            if (armed)
            {
                _nestedWrite!();
            }
        }
    }
}
