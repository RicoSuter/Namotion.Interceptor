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

    [Theory]
    [InlineData("record")]
    [InlineData("record struct")]
    public void WhenAttributeIsOnRecord_ThenNI0003IsReported(string recordKeyword)
    {
        // Arrange (case M)
        var source = $@"
using Namotion.Interceptor.Attributes;
namespace Repro
{{
    [InterceptorSubject]
    public partial {recordKeyword} NotAClass {{ }}
}}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(generated.GeneratorDiagnostics, d => d.Id == "NI0003");
        Assert.Empty(generated.Sources);
    }

    [Theory]
    [InlineData("struct")]
    [InlineData("interface")]
    public void WhenAttributeIsOnStructOrInterface_ThenGeneratorIsSilentAndCompilerReportsCS0592(string typeKeyword)
    {
        // Arrange: InterceptorSubjectAttribute's AttributeUsage is Class-only, so the compiler
        // already rejects this with CS0592. The generator's syntax-provider predicate excludes
        // struct and interface declarations entirely for performance, so it never runs its own
        // symbol lookup for them and, unlike for records, never reports NI0003 either.
        var source = $@"
using Namotion.Interceptor.Attributes;
namespace Repro
{{
    [InterceptorSubject]
    public partial {typeKeyword} NotAClass {{ }}
}}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.Empty(generated.GeneratorDiagnostics);
        Assert.Empty(generated.Sources);
        Assert.Contains(generated.CompilationErrors, d => d.Id == "CS0592");
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
        var diagnostic = Assert.Single(generated.GeneratorDiagnostics, d => d.Id == "NI0009");
        Assert.Equal("Interceptor subject 'Box' is generic, which is not supported", diagnostic.GetMessage());
        Assert.Empty(generated.Sources);
    }

    [Fact]
    public void WhenSubjectIsNestedInGenericContainingType_ThenNI0009NamesContainingTypeNotSubject()
    {
        // Arrange: the subject itself ("Inner") is not generic, but its containing type is. Roslyn
        // reports IsGenericType = true for a non-generic type nested inside a generic one, so the
        // diagnostic must not blame "Inner" for being generic; it must name "Outer" instead.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public partial class Outer<T>
    {
        [InterceptorSubject]
        public partial class Inner { }
    }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        var diagnostic = Assert.Single(generated.GeneratorDiagnostics, d => d.Id == "NI0009");
        Assert.Equal(
            "Interceptor subject 'Inner' is nested in generic containing type 'Outer', which is not supported",
            diagnostic.GetMessage());
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

    [Fact]
    public void WhenSubjectIsNonPartialAndGeneric_ThenNI0009IsReportedInsteadOfNI0001()
    {
        // Arrange: a non-partial generic subject is fundamentally unsupported (NI0009) regardless
        // of the fixable NI0001 (missing partial). Reporting NI0001 first sends the user on a
        // wasted round trip: add partial, rebuild, and only then learn generics are unsupported.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public class NonPartialGenericBox<T> { }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        var diagnostic = Assert.Single(generated.GeneratorDiagnostics);
        Assert.Equal("NI0009", diagnostic.Id);
        Assert.Empty(generated.Sources);
    }

    [Theory]
    [InlineData(@"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public partial class PlainSubject
    {
        public partial string Name { get; set; }
    }
}")]
    [InlineData(@"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public partial class Outer
    {
        [InterceptorSubject]
        public partial class Nested
        {
            public partial string Name { get; set; }
        }
    }
}")]
    [InlineData(@"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public partial record OuterRecord
    {
        [InterceptorSubject]
        public partial class Nested
        {
            public partial string Name { get; set; }
        }
    }
}")]
    [InlineData(@"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public abstract class GenericBase<T> { }

    [InterceptorSubject]
    public partial class DerivedFromGenericBase : GenericBase<string>
    {
        public partial string Name { get; set; }
    }
}")]
    public void WhenSubjectIsLegal_ThenNoDiagnosticsAreReportedAndExactlyOneSourceIsGenerated(string source)
    {
        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.Empty(generated.GeneratorDiagnostics);
        Assert.Single(generated.Sources);
    }
}
