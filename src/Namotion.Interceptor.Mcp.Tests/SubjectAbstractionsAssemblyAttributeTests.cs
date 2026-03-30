using Namotion.Interceptor.Attributes;

[assembly: SubjectAbstractionsAssembly]

namespace Namotion.Interceptor.Mcp.Tests;

public class SubjectAbstractionsAssemblyAttributeTests
{
    [Fact]
    public void WhenAssemblyMarked_ThenAttributeIsPresent()
    {
        // Arrange
        var assembly = typeof(SubjectAbstractionsAssemblyAttributeTests).Assembly;

        // Act
        var attribute = assembly.GetCustomAttributes(typeof(SubjectAbstractionsAssemblyAttribute), false);

        // Assert
        Assert.Single(attribute);
    }
}
