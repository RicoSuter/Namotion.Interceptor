using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;

namespace Namotion.Interceptor.Registry.Tests;

/// <summary>
/// Pins the write routing of dynamic properties added through
/// <see cref="RegisteredSubject.AddProperty"/>: their metadata setter loses the compile-time
/// property type (values travel boxed), so the routing must come from the declared type given at
/// registration. A scalar-declared dynamic property (source telemetry, say) must write on the
/// scalar route, while a subject-capable one must take the structural protocol.
/// </summary>
public class DynamicPropertyWriteRoutingTests
{
    /// <summary>
    /// A minimal faithful lifecycle that records its chain executions, the compile-time property
    /// type of each, and whether the commit happened inside its next() frame, so a test can
    /// observe which route a dynamic write took and that the lifecycle sits terminal-adjacent in
    /// the compiled chain.
    /// </summary>
    private sealed class ChainObservingLifecycle : ILifecycleInterceptor
    {
        public readonly List<Type> WrittenPropertyTypes = [];
        public readonly List<bool> CommitObservations = [];
        public List<string>? ExecutionLog;

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
            ExecutionLog?.Add("lifecycle");
            WrittenPropertyTypes.Add(typeof(TProperty));
            next(ref context);
            CommitObservations.Add(context.IsWritten);
        }
    }

    private sealed class OrderRecordingInterceptor(List<string> log) : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            log.Add("recorder");
            next(ref context);
        }
    }

    [Fact]
    public void WhenDynamicPropertyDeclaredTypeIsScalar_ThenWriteRunsTheChainOnTheScalarRoute()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var probe = new ChainObservingLifecycle();
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

        // Assert: the chain ran once with the declared scalar type even though the value arrived
        // boxed, which is what keeps the dynamic write off the structural protocol: the cached
        // typed delegate instantiated the write entry with the declared type, and that type
        // routes scalar.
        Assert.Equal([typeof(double)], probe.WrittenPropertyTypes);
        Assert.Equal([true], probe.CommitObservations);
        Assert.Equal(42.0, storedValue);
    }

    [Fact]
    public void WhenDynamicPropertyDeclaredTypeCanContainSubjects_ThenWriteTakesTheStructuralRouteWithTheLifecycleLast()
    {
        // Arrange: the recorder registers after the lifecycle and carries no ordering attributes,
        // so the chain partition must still execute it upstream of the lifecycle.
        var context = InterceptorSubjectContext.Create();
        var log = new List<string>();
        var probe = new ChainObservingLifecycle { ExecutionLog = log };
        context.AddService(probe);
        context.AddService<IWriteInterceptor>(new OrderRecordingInterceptor(log));

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

        // Assert: the subject-capable declared type takes the structural route (the chain ran
        // with the declared type, not object), the lifecycle executed terminal-adjacent, and the
        // commit happened inside its next() frame, which is the seam a racing attach or detach
        // orders against.
        Assert.Equal(["recorder", "lifecycle"], log);
        Assert.Equal([typeof(Person)], probe.WrittenPropertyTypes);
        Assert.Equal([true], probe.CommitObservations);
        Assert.Same(buddy, storedValue);
    }
}
