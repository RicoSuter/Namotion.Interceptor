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

    [Fact]
    public void WhenInterfaceHasDefaultIndexer_ThenIndexerIsSkipped()
    {
        // Arrange (case E)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IBag { string this[int i] => ""x""; }

    [InterceptorSubject]
    public partial class Bag : IBag { }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.DoesNotContain("this[]", generated.SingleSource());
    }

    [Fact]
    public void WhenInterfaceHasStaticProperty_ThenPropertyIsSkipped()
    {
        // Arrange (case V)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHasStatic { static string Version => ""1.0""; }

    [InterceptorSubject]
    public partial class Thing : IHasStatic { }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.DoesNotContain(@"[""Version""]", generated.SingleSource());
    }

    [Fact]
    public void WhenInterfaceHasPrivateDefaultMember_ThenMemberIsSkipped()
    {
        // Arrange (case W)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHasPrivate
    {
        double Value { get; set; }
        private string Hidden => ""h"";
    }

    [InterceptorSubject]
    public partial class Thing : IHasPrivate
    {
        public partial double Value { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.DoesNotContain(@"[""Hidden""]", generated.SingleSource());
    }

    [Fact]
    public void WhenInterfaceHasInternalDefaultMember_ThenMemberIsKept()
    {
        // Arrange (regression guard: internal members are reachable from generated code)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHasInternal
    {
        double Value { get; set; }
        internal string Status => ""s"";
        protected internal string Label => ""l"";
    }

    [InterceptorSubject]
    public partial class Thing : IHasInternal
    {
        public partial double Value { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var generatedSource = generated.SingleSource();
        Assert.Contains(@"[""Status""]", generatedSource);
        Assert.Contains(@"[""Label""]", generatedSource);
    }

    [Fact]
    public void WhenMethodIsNamedExactlyWithoutInterceptor_ThenMethodIsSkipped()
    {
        // Arrange (case O)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public partial class Thing
    {
        public void WithoutInterceptor() { }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.DoesNotContain("public void ()", generated.SingleSource());
    }

    [Fact]
    public void WhenWithoutInterceptorMethodIsUnsupportedShape_ThenMethodIsSkipped()
    {
        // Arrange (case Y: static, generic and by-reference parameters)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public partial class Thing
    {
        public static void StaticWithoutInterceptor() { }
        public void GenericWithoutInterceptor<T>(T value) { }
        public void RefWithoutInterceptor(ref int value) { }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var generatedSource = generated.SingleSource();
        Assert.DoesNotContain("public void Static(", generatedSource);
        Assert.DoesNotContain("public void Generic(", generatedSource);
        Assert.DoesNotContain("public void Ref(", generatedSource);
    }
}
