using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests;

#region Case S: a non-public subject

[InterceptorSubject]
internal partial class InternalSubject
{
    public partial string Name { get; set; }
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
}
