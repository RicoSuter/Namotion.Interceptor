namespace Namotion.Interceptor.Generator.Tests;

public class GeneratorShapeTests
{
    [Fact]
    public void WhenSubjectIsInGlobalNamespace_ThenGeneratedCodeCompiles()
    {
        // Arrange
        const string source = @"
using Namotion.Interceptor.Attributes;

[InterceptorSubject]
public partial class GlobalSubject
{
    public partial string Name { get; set; }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.DoesNotContain("YourDefaultNamespace", generated.SingleSource());
        Assert.Empty(generated.CompilationErrors);
    }
}
