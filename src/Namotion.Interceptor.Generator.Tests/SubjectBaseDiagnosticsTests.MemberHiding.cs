using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

public partial class SubjectBaseDiagnosticsTests
{
    [Fact]
    public void WhenDerivedSubjectDeclaresAGeneratedMemberName_ThenNI0063IsReported()
    {
        // Arrange: a 'new' annotated member of the same shape captures the generated call and
        // produces no compiler diagnostic at all, which is why the rule is name-only.
        const string source = """
            using System;
            using System.Collections.Generic;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class RootSubject
                {
                    public partial string RootName { get; set; }
                }

                [InterceptorSubject]
                public partial class LeafSubject : RootSubject
                {
                    public partial string LeafName { get; set; }

                    protected new IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties() => null;
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert: the capture is invisible to the compiler, so NI0063 is the only signal there is.
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0063");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenAnIntermediateClassDeclaresAPrivateGeneratedMemberName_ThenNI0063IsNotReported()
    {
        // Arrange: a private member on an intermediate neither hides nor is found by member lookup,
        // so nothing is captured and firing an error would be a pure false positive.
        const string source = """
            using System;
            using System.Collections.Generic;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class RootSubject
                {
                    public partial string RootName { get; set; }
                }

                public class PlainMiddle : RootSubject
                {
                    private string InvokeMethod = "";
                }

                [InterceptorSubject]
                public partial class LeafSubject : PlainMiddle
                {
                    public partial string LeafName { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "NI0063");
    }

    [Fact]
    public void WhenDerivedSubjectDeclaresAStaticGeneratedMemberName_ThenNI0063IsReportedAndTheCallIsCaptured()
    {
        // Arrange: C# hiding is not staticness sensitive, and calling a static by simple name from an
        // instance body is legal, so this captures the generated call with no compiler diagnostic.
        var source = LeafDeclaring(
            "private new static IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties() => null;");

        // Act
        var result = GeneratorTestHost.RunForExecution(source);
        var leaf = (IInterceptorSubject)result.CreateInstance("Repro.LeafSubject");
        leaf.AddProperties(new SubjectPropertyMetadata(
            "Extra", typeof(string), [], _ => "e", (_, _) => { }, isIntercepted: false, isDynamic: true));

        // Assert: the added property is swallowed, and NI0063 is the only signal there is.
        Assert.False(leaf.Properties.ContainsKey("Extra"));
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0063");
    }

    [Fact]
    public void WhenDerivedSubjectDeclaresNothingUnusual_ThenNeitherRuleFiresAndAddedPropertiesSurvive()
    {
        // Arrange: the control for the capture and hijack cases below.
        var source = LeafDeclaring("");

        // Act
        var result = GeneratorTestHost.RunForExecution(source);
        var leaf = (IInterceptorSubject)result.CreateInstance("Repro.LeafSubject");
        leaf.AddProperties(new SubjectPropertyMetadata(
            "Extra", typeof(string), [], _ => "e", (_, _) => { }, isIntercepted: false, isDynamic: true));

        // Assert
        Assert.True(leaf.Properties.ContainsKey("Extra"));
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id is "NI0063" or "NI0064");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }
}
