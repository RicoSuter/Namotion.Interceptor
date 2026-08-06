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
}
