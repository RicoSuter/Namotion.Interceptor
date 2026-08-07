using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

public class SubjectBaseShapeTests
{
    [Fact]
    public void WhenBaseImplementsRaisePropertyChangedWithoutBeingASubject_ThenNoNotifyPlumbingIsRedeclared()
    {
        // Arrange: the base is INPC + IRaisePropertyChanged but NOT IInterceptorSubject and has no
        // attribute, so it is not a subject ancestor. BaseClassHasInpc must still be true, because
        // its second disjunct is asked of the subject, not of the ancestor.
        const string source = """
            using System.ComponentModel;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public abstract class ManualBase : INotifyPropertyChanged, IRaisePropertyChanged
                {
                    public event PropertyChangedEventHandler? PropertyChanged;

                    public void RaisePropertyChanged(string propertyName)
                        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
                }

                [InterceptorSubject]
                public partial class ManualDerived : ManualBase
                {
                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);
        var generated = result.SingleSource();

        // Assert
        Assert.DoesNotContain("public event PropertyChangedEventHandler? PropertyChanged;", generated);
        Assert.DoesNotContain("protected void RaisePropertyChanged(string propertyName)", generated);
        Assert.Contains("((IRaisePropertyChanged)this).RaisePropertyChanged(nameof(Name))", generated);
    }

    [Fact]
    public void WhenSubjectIsSealedAndDerived_ThenItCompilesWithoutWarnings()
    {
        // Arrange: a sealed DERIVED subject is legal today, because RaisePropertyChanged is gated
        // on BaseClassHasInpc and so is not emitted into it. Only a sealed ROOT fails (Task 3).
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class BaseSubject
                {
                    public partial string BaseName { get; set; }
                }

                [InterceptorSubject]
                public sealed partial class SealedLeaf : BaseSubject
                {
                    public partial string LeafName { get; set; }
                }
            }
            """;

        // Act & Assert
        GeneratorTestHost.RunExpectingNoWarnings(source);
    }

    [Fact]
    public void WhenAPlainClassSitsBetweenTwoSubjects_ThenTheDerivedSubjectCompilesAndMergesBaseProperties()
    {
        // Arrange: A is a subject, B is an ordinary class, C is a subject. At generation time B
        // neither carries the attribute nor implements IInterceptorSubject, because A's interface
        // list lives only in A.g.cs, so the immediate base tells the generator nothing.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class A
                {
                    public partial string P { get; set; }
                }

                public class B : A { }

                [InterceptorSubject]
                public partial class C : B
                {
                    public partial string Q { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);
        var derived = Assert.Single(result.Sources, s => s.HintName.Contains("Repro.C.g.cs")).SourceText.ToString();

        // Assert: the base facts come from A, not from B.
        Assert.Contains("public new static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties", derived);
        Assert.Contains(".Concat(global::Repro.A.DefaultProperties)", derived);
        Assert.DoesNotContain("public event PropertyChangedEventHandler? PropertyChanged;", derived);
    }

    [Fact]
    public void WhenAPlainClassSitsBetweenTwoSubjectsAcrossAssemblies_ThenTheWalkSkipsItAndNamesTheAttributedAncestor()
    {
        // Arrange: same A/B/C shape as above, but A and B live in a referenced assembly whose
        // generated code is already in metadata. That is what separates SubjectBaseContract's
        // Interfaces from AllInterfaces: B inherits IInterceptorSubject from A, so AllInterfaces
        // reports it on B and the walk would stop at the plain intermediate. The result still
        // compiles, so only the emitted shape asserted below catches the regression.
        const string librarySource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Lib
            {
                [InterceptorSubject]
                public partial class A
                {
                    public partial string P { get; set; }
                }

                public class B : A { }
            }
            """;
        const string mainSource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class C : Lib.B
                {
                    public partial string Q { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunWithLibraryReference(librarySource, mainSource, runGeneratorOverLibrary: true);
        var derived = result.SingleSource();

        // Assert
        Assert.True(
            result.CompilationErrors.Count == 0,
            "Generated code did not compile:" + Environment.NewLine +
            string.Join(Environment.NewLine, result.CompilationErrors.Select(d => d.ToString())));
        Assert.Contains(".Concat(global::Lib.A.DefaultProperties)", derived);
        Assert.DoesNotContain("((IRaisePropertyChanged)this).RaisePropertyChanged", derived);
    }

    [Fact]
    public void WhenAPlainClassSitsBetweenAHandWrittenSubjectAndTheSubject_ThenTheWalkResolvesTheHandWrittenClass()
    {
        // Arrange: the ancestor carries no attribute and never names IInterceptorSubject directly,
        // it names IMySubject which derives from it. Only the transitive check on each declared
        // interface recognises that class as a subject.
        const string librarySource = """
            using System.Collections.Concurrent;
            using System.Collections.Generic;
            using Namotion.Interceptor;

            namespace Lib
            {
                public interface IMySubject : IInterceptorSubject { }

                public class HandWrittenSubject : IMySubject
                {
                    public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; } =
                        new Dictionary<string, SubjectPropertyMetadata>();

                    public object SyncRoot { get; } = new object();
                    public IInterceptorSubjectContext Context => throw new System.NotSupportedException();
                    public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();
                    public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => DefaultProperties;
                    public void AddProperties(IEnumerable<SubjectPropertyMetadata> properties) { }
                }

                public class PlainInBetween : HandWrittenSubject { }
            }
            """;
        const string mainSource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class Derived : Lib.PlainInBetween
                {
                    public partial string Q { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunWithLibraryReferenceExpectingCleanCompilation(librarySource, mainSource);
        var derived = result.SingleSource();

        // Assert
        Assert.Contains(".Concat(global::Lib.HandWrittenSubject.DefaultProperties)", derived);
        Assert.DoesNotContain("global::Lib.PlainInBetween", derived);
    }
}
