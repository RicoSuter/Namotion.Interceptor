using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Generator.Tests;

#region Case S: a non-public subject

[InterceptorSubject]
internal partial class InternalSubject
{
    public partial string Name { get; set; }
}

#endregion

#region Case W: internal and protected internal default members stay supported

public interface IAccessibleDefaults
{
    double Value { get; set; }

    internal string InternalStatus => "internal-" + Value;

    protected internal string ProtectedInternalStatus => "protected-internal-" + Value;
}

[InterceptorSubject]
public partial class AccessibleDefaultsSubject : IAccessibleDefaults
{
    public partial double Value { get; set; }
}

#endregion

#region Case P: a subject nested in a record

public partial record RecordContainer
{
    [InterceptorSubject]
    public partial class NestedSubject
    {
        public partial string Name { get; set; }
    }
}

#endregion

#region Case Y: a "ref readonly" parameter on a WithoutInterceptor method

[InterceptorSubject]
public partial class RefReadonlyMethodSubject
{
    public partial int Received { get; set; }

    public void SendWithoutInterceptor(ref readonly int value)
    {
        Received = value;
    }

    public int DoubleWithoutInterceptor(ref readonly int value)
    {
        return value * 2;
    }
}

#endregion

#region Case Y2: an "in" parameter on a WithoutInterceptor method

[InterceptorSubject]
public partial class InParameterMethodSubject
{
    public partial int Received { get; set; }

    public void SendWithoutInterceptor(in int value)
    {
        Received = value;
    }
}

#endregion

public class GeneratorShapeBehaviorTests
{
    [Fact]
    public void WhenSubjectIsInternal_ThenPropertiesAreTracked()
    {
        // Arrange
        var readInterceptor = new RecordingReadInterceptor();
        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithService(() => readInterceptor)
            .WithService(() => writeInterceptor);

        var subject = new InternalSubject(context);
        var firedEvents = new List<string>();
        subject.PropertyChanged += (_, e) => firedEvents.Add(e.PropertyName!);

        // Act
        subject.Name = "value";
        var value = subject.Name;

        // Assert
        Assert.Equal("value", value);
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "Name" && Equals(write.Value, "value"));
        Assert.Contains(readInterceptor.Reads, read => read.PropertyName == "Name" && Equals(read.Value, "value"));
        Assert.Equal(["Name"], firedEvents);

        var registeredSubject = subject.TryGetRegisteredSubject();
        Assert.NotNull(registeredSubject);
        var nameProperty = registeredSubject.TryGetProperty("Name");
        Assert.NotNull(nameProperty);
        Assert.Equal("value", nameProperty.GetValue());
    }

    [Fact]
    public void WhenDefaultMemberIsInternalOrProtectedInternal_ThenItRemainsExposed()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var subject = new AccessibleDefaultsSubject(context) { Value = 3 };

        // Act
        var properties = ((IInterceptorSubject)subject).Properties;
        var registeredSubject = subject.TryGetRegisteredSubject();

        // Assert
        Assert.Equal("internal-3", properties["InternalStatus"].GetValue?.Invoke(subject));
        Assert.Equal("protected-internal-3", properties["ProtectedInternalStatus"].GetValue?.Invoke(subject));

        Assert.NotNull(registeredSubject);
        var internalStatusProperty = registeredSubject.TryGetProperty("InternalStatus");
        var protectedInternalStatusProperty = registeredSubject.TryGetProperty("ProtectedInternalStatus");
        Assert.NotNull(internalStatusProperty);
        Assert.NotNull(protectedInternalStatusProperty);
        Assert.Equal("internal-3", internalStatusProperty.GetValue());
        Assert.Equal("protected-internal-3", protectedInternalStatusProperty.GetValue());
    }

    [Fact]
    public void WhenSubjectIsNestedInRecord_ThenPropertiesAreTracked()
    {
        // Arrange
        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithService(() => writeInterceptor);

        var subject = new RecordContainer.NestedSubject(context);
        var firedEvents = new List<string>();
        subject.PropertyChanged += (_, e) => firedEvents.Add(e.PropertyName!);

        // Act
        subject.Name = "value";

        // Assert
        Assert.Equal("value", subject.Name);
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "Name" && Equals(write.Value, "value"));
        Assert.Equal(["Name"], firedEvents);

        var registeredSubject = subject.TryGetRegisteredSubject();
        Assert.NotNull(registeredSubject);
        var nameProperty = registeredSubject.TryGetProperty("Name");
        Assert.NotNull(nameProperty);
        Assert.Equal("value", nameProperty.GetValue());
    }

    [Fact]
    public void WhenWithoutInterceptorMethodTakesRefReadonlyParameter_ThenTheWrapperRoutesThroughTheMethodInterceptor()
    {
        // Arrange: the wrapper boxes the argument and passes a readonly reference to the unboxed
        // temporary, so the value has to arrive unchanged in the wrapped method, and the call must
        // still be visible to the method interceptor chain rather than bypass it.
        var methodInterceptor = new RecordingMethodInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => methodInterceptor);

        var subject = new RefReadonlyMethodSubject(context);

        // Act
        subject.Send(42);
        var doubled = subject.Double(21);

        // Assert
        Assert.Equal(42, subject.Received);
        Assert.Equal(42, doubled);
        Assert.Contains(methodInterceptor.Invocations,
            invocation => invocation.MethodName == "Send" && invocation.Parameters.SequenceEqual(new object?[] { 42 }));
        Assert.Contains(methodInterceptor.Invocations,
            invocation => invocation.MethodName == "Double" && invocation.Parameters.SequenceEqual(new object?[] { 21 }));
    }

    [Fact]
    public void WhenWithoutInterceptorMethodTakesInParameter_ThenTheWrapperRoutesThroughTheMethodInterceptor()
    {
        // Arrange: the "in" argument is passed by value into the generated wrapper, which forwards
        // it to the wrapped method through the "in" parameter, and the call must be visible to the
        // method interceptor chain rather than bypass it.
        var methodInterceptor = new RecordingMethodInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => methodInterceptor);

        var subject = new InParameterMethodSubject(context);

        // Act
        subject.Send(42);

        // Assert
        Assert.Equal(42, subject.Received);
        Assert.Contains(methodInterceptor.Invocations,
            invocation => invocation.MethodName == "Send" && invocation.Parameters.SequenceEqual(new object?[] { 42 }));
    }

    private sealed class RecordingReadInterceptor : IReadInterceptor
    {
        public List<(string PropertyName, object? Value)> Reads { get; } = [];

        public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
        {
            var value = next(ref context);
            Reads.Add((context.Property.Name, value));
            return value;
        }
    }

    private sealed class RecordingWriteInterceptor : IWriteInterceptor
    {
        public List<(string PropertyName, object? Value)> Writes { get; } = [];

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            Writes.Add((context.Property.Name, context.NewValue));
            next(ref context);
        }
    }

    private sealed class RecordingMethodInterceptor : IMethodInterceptor
    {
        public List<(string MethodName, object?[] Parameters)> Invocations { get; } = [];

        public object? InvokeMethod(MethodInvocationContext context, InvokeMethodInterceptionDelegate next)
        {
            Invocations.Add((context.MethodName, context.Parameters));
            return next(ref context);
        }
    }
}
