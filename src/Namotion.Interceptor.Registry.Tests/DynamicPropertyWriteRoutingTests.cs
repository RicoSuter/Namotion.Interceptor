using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;

namespace Namotion.Interceptor.Registry.Tests;

/// <summary>
/// Pins the write routing of dynamic properties added through
/// <see cref="RegisteredSubject.AddProperty"/>: their metadata setter loses the compile-time
/// property type (values travel boxed), so the routing must come from the declared type given at
/// registration. Both scalar and subject-capable dynamic properties must execute the configured
/// lifecycle chain and write the supplied value.
/// </summary>
public class DynamicPropertyWriteRoutingTests
{
    /// <summary>
    /// A minimal lifecycle that counts write-chain executions.
    /// </summary>
    private sealed class CountingLifecycle : ILifecycleInterceptor
    {
        public int WritePropertyCount;

        public bool TryAddProperties(SubjectPropertyRegistration registration)
        {
            registration.Publish();
            return true;
        }

        public void AttachSubjectToContext(IInterceptorSubject subject, IInterceptorSubjectContext context, SubjectAttachmentAnchorKind anchor)
        {
            var executor = subject.Executor;
            executor.TryGetAttachment(out _, out _, out var revision);
            executor.TryUpdateAttachment(revision, context, anchor, out _);
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject, IInterceptorSubjectContext context)
        {
            var executor = subject.Executor;
            executor.TryGetAttachment(out _, out _, out var revision);
            executor.TryUpdateAttachment(revision, null, SubjectAttachmentAnchorKind.None, out _);
        }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            Interlocked.Increment(ref WritePropertyCount);
            next(ref context);
        }
    }

    [Fact]
    public void WhenDynamicPropertyDeclaredTypeIsScalar_ThenWriteUsesTheLifecycleChain()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var probe = new CountingLifecycle();
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

        // Registration publishes the initial value as a null-to-value write, which routes as
        // object. This test pins the subsequent typed dynamic write.
        var writesAfterRegistration = probe.WritePropertyCount;

        // Act
        person.Properties["Temperature"].SetValue!(person, 42.0);

        // Assert
        Assert.Equal(writesAfterRegistration + 1, probe.WritePropertyCount);
        Assert.Equal(42.0, storedValue);
    }

    [Fact]
    public void WhenDynamicPropertyDeclaredTypeCanContainSubjects_ThenWriteUsesTheLifecycleChain()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var probe = new CountingLifecycle();
        context.AddService(probe);

        IInterceptorSubject person = new Person();
        person.AttachToContext(context);

        var registeredSubject = new RegisteredSubject(person);
        object? storedValue = null;
        registeredSubject.AddProperty(
            "Buddy",
            typeof(Person),
            _ => storedValue,
            (_, value) => storedValue = value);

        IInterceptorSubject buddy = new Person();

        // Act
        person.Properties["Buddy"].SetValue!(person, buddy);

        // Assert
        Assert.Equal(1, probe.WritePropertyCount);
        Assert.Same(buddy, storedValue);
    }
}
