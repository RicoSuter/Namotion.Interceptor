using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tests.Attributes;

public class InterceptedAttributeTests
{
    [Fact]
    public void InterceptedAttribute_CanBeAppliedToProperty()
    {
        // Arrange
        var attribute = new InterceptedAttribute();

        // Assert
        Assert.NotNull(attribute);
    }

    [Fact]
    public void InterceptedAttribute_HasCorrectAttributeUsage()
    {
        // Arrange
        var attributeUsage = typeof(InterceptedAttribute)
            .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .Cast<AttributeUsageAttribute>()
            .Single();

        // Assert
        Assert.Equal(AttributeTargets.Property, attributeUsage.ValidOn);
        Assert.False(attributeUsage.AllowMultiple);
    }
}
