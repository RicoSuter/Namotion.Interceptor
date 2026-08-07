using Namotion.Interceptor.Attributes;

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

public class GeneratorShapeBehaviorTests
{
    [Fact]
    public void WhenSubjectIsInternal_ThenPropertiesAreTracked()
    {
        // Arrange
        var subject = new InternalSubject { Name = "value" };

        // Act
        var properties = ((IInterceptorSubject)subject).Properties;

        // Assert
        Assert.Equal("value", properties["Name"].GetValue?.Invoke(subject));
    }

    [Fact]
    public void WhenDefaultMemberIsInternalOrProtectedInternal_ThenItRemainsExposed()
    {
        // Arrange
        var subject = new AccessibleDefaultsSubject { Value = 3 };

        // Act
        var properties = ((IInterceptorSubject)subject).Properties;

        // Assert
        Assert.Equal("internal-3", properties["InternalStatus"].GetValue?.Invoke(subject));
        Assert.Equal("protected-internal-3", properties["ProtectedInternalStatus"].GetValue?.Invoke(subject));
    }

    [Fact]
    public void WhenSubjectIsNestedInRecord_ThenPropertiesAreTracked()
    {
        // Arrange
        var subject = new RecordContainer.NestedSubject { Name = "value" };

        // Act
        var properties = ((IInterceptorSubject)subject).Properties;

        // Assert
        Assert.Equal("value", properties["Name"].GetValue?.Invoke(subject));
    }

    [Fact]
    public void WhenWithoutInterceptorMethodTakesRefReadonlyParameter_ThenTheWrapperForwardsTheValue()
    {
        // Arrange: the wrapper boxes the argument and passes a readonly reference to the unboxed
        // temporary, so the value has to arrive unchanged in the wrapped method.
        var subject = new RefReadonlyMethodSubject();

        // Act
        subject.Send(42);
        var doubled = subject.Double(21);

        // Assert
        Assert.Equal(42, subject.Received);
        Assert.Equal(42, doubled);
    }
}
