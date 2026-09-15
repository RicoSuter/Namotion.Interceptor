using System.Reflection;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

public partial class SubjectBaseShapeTests
{
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
        // generated code is already in metadata. That is what separates SubjectAncestry's
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
    public void WhenAPlainClassSitsBetweenTwoSubjectsAcrossAssemblies_ThenABaseDeclaredWriteReachesTheInterceptor()
    {
        // Arrange: the shape the nearest-subject-ancestor walk exists for, executed. Naming the
        // right ancestor in the emitted text is only half the claim; the other half is that the
        // ancestor's setter, compiled into the library one class above a plain intermediate, ends
        // up on the leaf's executor.
        const string librarySource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Library
            {
                [InterceptorSubject]
                public partial class LibraryBase
                {
                    public partial string BaseName { get; set; }
                }

                public class PlainInBetween : LibraryBase
                {
                }
            }
            """;

        const string mainSource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace App
            {
                [InterceptorSubject]
                public partial class AppLeaf : Library.PlainInBetween
                {
                    public partial string LeafName { get; set; }
                }
            }
            """;

        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => writeInterceptor);

        var result = GeneratorTestHost.RunWithLibraryReferenceForExecution(librarySource, mainSource);
        var leafType = result.LoadAssembly().GetType("App.AppLeaf");
        Assert.NotNull(leafType);
        var leaf = (IInterceptorSubject)Activator.CreateInstance(leafType, context)!;

        // Act
        leafType.GetProperty("BaseName")!.SetValue(leaf, "base-written");
        leafType.GetProperty("LeafName")!.SetValue(leaf, "leaf-written");

        // Assert
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "BaseName" && Equals(write.Value, "base-written"));
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "LeafName" && Equals(write.Value, "leaf-written"));
        Assert.Contains("BaseName", leaf.Properties.Keys);
        Assert.Contains("LeafName", leaf.Properties.Keys);
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
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

        // Assert: the hand-written ancestor exposes a usable DefaultProperties but none of the
        // helpers, so it takes the NI0062 root-mode fallback. Pinned here because the mode is not
        // otherwise visible in the emitted shape, and it must not flip back unnoticed.
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0062");
        Assert.Contains(".Concat(global::Lib.HandWrittenSubject.DefaultProperties)", derived);
        Assert.DoesNotContain("global::Lib.PlainInBetween", derived);
    }

    [Fact]
    public void WhenBaseSubjectIsInAReferencedAssembly_ThenTheDerivedSubjectSharesItsInterceptionMembers()
    {
        // Arrange: mode selection branch 2. The library is compiled WITH the generator, so its
        // protected helpers exist as metadata symbols the contract check can see.
        const string librarySource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Library
            {
                [InterceptorSubject]
                public partial class LibraryBase
                {
                    public partial string BaseName { get; set; }
                }
            }
            """;

        const string mainSource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace App
            {
                [InterceptorSubject]
                public partial class AppLeaf : Library.LibraryBase
                {
                    public partial string LeafName { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunWithLibraryReference(librarySource, mainSource, runGeneratorOverLibrary: true);
        var generated = result.SingleSource();

        // Assert: derived mode, so no interception members of its own. Both members below are emitted by root
        // mode only, unlike the Properties line, which both modes emit identically.
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        Assert.DoesNotContain("private IInterceptorExecutor? _context;", generated);
        Assert.DoesNotContain("void IInterceptorSubject.AddProperties", generated);
    }

    [Fact]
    public void WhenBaseSubjectIsInAReferencedAssembly_ThenABaseDeclaredWriteReachesTheInterceptor()
    {
        // Arrange: the same shape as above, executed. The emitted text cannot show this: the base
        // property's setter was compiled into the library against the library's own interception members, and
        // only running it shows that it reaches the executor the leaf's context published rather
        // than a second one the leaf kept for itself. This is what a consumer deriving from a
        // subject shipped in a package hits, and the contract check reads the base from metadata
        // here rather than from source.
        const string librarySource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Library
            {
                [InterceptorSubject]
                public partial class LibraryBase
                {
                    public partial string BaseName { get; set; }
                }
            }
            """;

        const string mainSource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace App
            {
                [InterceptorSubject]
                public partial class AppLeaf : Library.LibraryBase
                {
                    public partial string LeafName { get; set; }
                }
            }
            """;

        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => writeInterceptor);

        var result = GeneratorTestHost.RunWithLibraryReferenceForExecution(librarySource, mainSource);
        var leafType = result.LoadAssembly().GetType("App.AppLeaf");
        Assert.NotNull(leafType);
        var leaf = (IInterceptorSubject)Activator.CreateInstance(leafType, context)!;

        // Act
        leafType.GetProperty("BaseName")!.SetValue(leaf, "base-written");
        leafType.GetProperty("LeafName")!.SetValue(leaf, "leaf-written");

        // Assert
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "BaseName" && Equals(write.Value, "base-written"));
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "LeafName" && Equals(write.Value, "leaf-written"));
        Assert.Contains("BaseName", leaf.Properties.Keys);
        Assert.Contains("LeafName", leaf.Properties.Keys);
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenSubjectsAreInternalAndNested_ThenTheLeafTakesDerivedModeWithItsDeclaredAccessibility()
    {
        // Arrange: accessibility is checked with IsSymbolAccessibleWithin, and nested containing
        // types are re-declared by the generator, so both interact with the derived-mode split.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public partial class Container
                {
                    [InterceptorSubject]
                    internal partial class NestedRoot
                    {
                        public partial string RootName { get; set; }
                    }

                    [InterceptorSubject]
                    private protected partial class NestedLeaf : NestedRoot
                    {
                        public partial string LeafName { get; set; }
                    }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);
        var leaf = Assert.Single(result.Sources, s => s.HintName.Contains("NestedLeaf")).SourceText.ToString();

        // Assert: the private protected leaf reaches the internal root's protected helpers, so it
        // still takes derived mode through two re-declared containing types.
        Assert.Contains("private protected partial class NestedLeaf : IInterceptorSubject", leaf);
        Assert.DoesNotContain("private IInterceptorExecutor? _context;", leaf);
    }
}
