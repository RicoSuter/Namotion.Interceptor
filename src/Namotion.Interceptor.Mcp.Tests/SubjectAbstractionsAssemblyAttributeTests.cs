using Namotion.Interceptor;
using Xunit;

[assembly: SubjectAbstractionsAssembly]

namespace Namotion.Interceptor.Mcp.Tests;

public class SubjectAbstractionsAssemblyAttributeTests
{
    [Fact]
    public void Attribute_can_be_applied_to_assembly()
    {
        var attribute = typeof(SubjectAbstractionsAssemblyAttributeTests).Assembly
            .GetCustomAttributes(typeof(SubjectAbstractionsAssemblyAttribute), false);

        Assert.Single(attribute);
    }
}
