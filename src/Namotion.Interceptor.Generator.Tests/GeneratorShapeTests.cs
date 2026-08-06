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
    public void WhenExplicitImplementationTargetsProtectedMember_ThenMemberIsSkipped()
    {
        // Arrange (a protected interface member cannot be reached even through an explicit
        // implementation: Roslyn reports the implementation itself as Private, but the
        // accessibility that governs reachability is the implemented member's)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHuman { protected string Secret { get; } }
    public interface IMale : IHuman { string IHuman.Secret => ""m""; }

    [InterceptorSubject]
    public partial class John : IMale { }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.DoesNotContain(@"[""Secret""]", generated.SingleSource());
    }

    [Fact]
    public void WhenInterfaceDefaultMemberIsInternalInReferencedAssembly_ThenMemberIsSkipped()
    {
        // Arrange: the "same assembly" premise the accessibility check used to rely on does not
        // hold when the interface is declared in a referenced assembly; the generated code lives
        // in a different assembly with no InternalsVisibleTo, so an internal member is unreachable
        // there (CS0122) even though it would be reachable if declared locally.
        const string librarySource = @"
public interface IFace
{
    internal string Probe => ""p"";
}";
        const string mainSource = @"
using Namotion.Interceptor.Attributes;

[InterceptorSubject]
public partial class Thing : IFace
{
}";

        // Act
        var generated = GeneratorTestHost.RunWithLibraryReferenceExpectingCleanCompilation(librarySource, mainSource);

        // Assert
        Assert.DoesNotContain(@"[""Probe""]", generated.SingleSource());
    }

    [Fact]
    public void WhenInterfaceDefaultMemberIsProtectedInternalInReferencedAssembly_ThenMemberIsSkipped()
    {
        // Arrange: cross-assembly, "protected internal" fails both halves of its own rule: the
        // internal half fails because the generated assembly has no InternalsVisibleTo, and the
        // protected half fails because the generated code accesses the member through a cast to
        // the interface type, not through the subject type itself (CS1540).
        const string librarySource = @"
public interface IFace
{
    protected internal string Probe => ""p"";
}";
        const string mainSource = @"
using Namotion.Interceptor.Attributes;

[InterceptorSubject]
public partial class Thing : IFace
{
}";

        // Act
        var generated = GeneratorTestHost.RunWithLibraryReferenceExpectingCleanCompilation(librarySource, mainSource);

        // Assert
        Assert.DoesNotContain(@"[""Probe""]", generated.SingleSource());
    }

    [Fact]
    public void WhenInterfaceDefaultMemberHasPrivateSetter_ThenSetterIsDroppedButGetterIsKept()
    {
        // Arrange: the property itself is public (interface members default to public), but its
        // setter is individually private, so only the getter is reachable from generated code.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHasPrivateSetter
    {
        double Value { get; set; }
        string Probe { get => ""a""; private set { } }
    }

    [InterceptorSubject]
    public partial class Thing : IHasPrivateSetter
    {
        public partial double Value { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var generatedSource = generated.SingleSource();
        Assert.Contains(@"[""Probe""]", generatedSource);
        Assert.Contains("(o) => ((global::Repro.IHasPrivateSetter)o).Probe", generatedSource);
        Assert.DoesNotContain(".Probe = ", generatedSource);
    }

    [Fact]
    public void WhenInterfaceDefaultMemberHasPrivateGetter_ThenGetterIsDroppedButSetterIsKept()
    {
        // Arrange: mirror of the previous case, with the private accessor on the getter instead.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHasPrivateGetter
    {
        double Value { get; set; }
        string Probe { private get => ""a""; set { } }
    }

    [InterceptorSubject]
    public partial class Thing : IHasPrivateGetter
    {
        public partial double Value { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var generatedSource = generated.SingleSource();
        Assert.Contains(@"[""Probe""]", generatedSource);
        Assert.DoesNotContain("(o) => ((global::Repro.IHasPrivateGetter)o).Probe", generatedSource);
        Assert.Contains(".Probe = ", generatedSource);
    }

    [Fact]
    public void WhenInterfaceDefaultMemberHasProtectedSetter_ThenSetterIsDroppedButGetterIsKept()
    {
        // Arrange: same reasoning as the protected-member case, but scoped to a single accessor:
        // generated code accesses the member through a cast to the interface type, which is never
        // a valid qualifying expression for protected access (CS1540).
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHasProtectedSetter
    {
        double Value { get; set; }
        string Probe { get => ""a""; protected set { } }
    }

    [InterceptorSubject]
    public partial class Thing : IHasProtectedSetter
    {
        public partial double Value { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var generatedSource = generated.SingleSource();
        Assert.Contains(@"[""Probe""]", generatedSource);
        Assert.Contains("(o) => ((global::Repro.IHasProtectedSetter)o).Probe", generatedSource);
        Assert.DoesNotContain(".Probe = ", generatedSource);
    }

    [Fact]
    public void WhenInterfaceDefaultMemberHasInternalSetter_ThenSetterIsKept()
    {
        // Arrange (regression guard): an internal setter in the same assembly stays reachable,
        // same as a whole internal member does.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHasInternalSetter
    {
        double Value { get; set; }
        string Probe { get => ""a""; internal set { } }
    }

    [InterceptorSubject]
    public partial class Thing : IHasInternalSetter
    {
        public partial double Value { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var generatedSource = generated.SingleSource();
        Assert.Contains(@"[""Probe""]", generatedSource);
        Assert.Contains("(o) => ((global::Repro.IHasInternalSetter)o).Probe", generatedSource);
        Assert.Contains(".Probe = ", generatedSource);
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
        // Arrange (case Y: static, generic, by-reference parameters, and explicit interface implementation)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IFoo
    {
        void DoWithoutInterceptor();
    }

    [InterceptorSubject]
    public partial class Thing : IFoo
    {
        public static void StaticWithoutInterceptor() { }
        public void GenericWithoutInterceptor<T>(T value) { }
        public void RefWithoutInterceptor(ref int value) { }
        void IFoo.DoWithoutInterceptor() { }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var generatedSource = generated.SingleSource();
        Assert.DoesNotContain("public void Static(", generatedSource);
        Assert.DoesNotContain("public void Generic(", generatedSource);
        Assert.DoesNotContain("public void Ref(", generatedSource);
        Assert.DoesNotContain("public void Do(", generatedSource);
    }
}
