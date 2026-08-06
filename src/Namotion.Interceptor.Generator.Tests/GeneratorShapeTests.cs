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

    [Fact]
    public void WhenSubjectIsInternal_ThenGeneratedCodeCompiles()
    {
        // Arrange (case S)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    internal partial class InternalSubject
    {
        public partial string Name { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains("internal partial class InternalSubject", generated.SingleSource());
        Assert.Empty(generated.CompilationErrors);
    }
}
