using Microsoft.CodeAnalysis;

namespace Namotion.Interceptor.Generator.Tests;

public class DiagnosticTests
{
    [Fact]
    public void WhenSubjectIsNotPartial_ThenNI0001IsReported()
    {
        // Arrange (case K)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public class NotPartial { }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(generated.GeneratorDiagnostics, d => d.Id == "NI0001");
        Assert.Equal(DiagnosticSeverity.Error, generated.GeneratorDiagnostics.Single(d => d.Id == "NI0001").Severity);
        Assert.Empty(generated.Sources);
    }

    [Fact]
    public void WhenContainingTypeIsNotPartial_ThenNI0002IsReported()
    {
        // Arrange (case L)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public class Outer
    {
        [InterceptorSubject]
        public partial class Nested { }
    }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(generated.GeneratorDiagnostics, d => d.Id == "NI0002");
        Assert.Empty(generated.Sources);
    }

    [Fact]
    public void WhenAttributeIsOnRecord_ThenNI0003IsReported()
    {
        // Arrange (case M)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public partial record NotAClass { }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(generated.GeneratorDiagnostics, d => d.Id == "NI0003");
        Assert.Empty(generated.Sources);
    }

    [Fact]
    public void WhenSubjectIsGeneric_ThenNI0009IsReported()
    {
        // Arrange (case T)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public partial class Box<T>
    {
        public partial string Name { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(generated.GeneratorDiagnostics, d => d.Id == "NI0009");
        Assert.Empty(generated.Sources);
    }

    [Fact]
    public void WhenSubjectIsFileLocal_ThenNI0010IsReported()
    {
        // Arrange (case X)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    file partial class FileLocal
    {
        public partial string Name { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(generated.GeneratorDiagnostics, d => d.Id == "NI0010");
        Assert.Empty(generated.Sources);
    }
}
