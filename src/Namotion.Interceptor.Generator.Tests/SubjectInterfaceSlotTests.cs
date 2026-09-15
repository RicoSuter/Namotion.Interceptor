using Namotion.Interceptor.Tracking;
using Xunit;
using static Namotion.Interceptor.Generator.Tests.SubjectInheritanceTestSources;

namespace Namotion.Interceptor.Generator.Tests;

public class SubjectInterfaceSlotTests
{
    [Fact]
    public void WhenDerivedSubjectDeclaresAPublicSyncRoot_ThenNI0064IsReported()
    {
        // Arrange: this compiles clean today, because the derived class emits its own explicit
        // implementation which wins. After the split it takes the interface slot.
        const string source = """
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

                    public object SyncRoot { get; } = new object();
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0064");
    }

    [Fact]
    public void WhenDerivedSubjectDeclaresAPartialPropertyNamedLikeAnInterfaceMember_ThenNI0064IsNotReported()
    {
        // Arrange: a string Data cannot implement IInterceptorSubject.Data, so the root keeps the
        // slot. Every subject used to emit its own explicit implementation, so a property named Data
        // compiled and worked before, and Data is a plausible name on an industrial model.
        var source = LeafDeclaring("public partial string Data { get; set; }");

        // Act
        var result = GeneratorTestHost.RunForExecution(source);
        var leaf = (IInterceptorSubject)result.CreateInstance("Repro.LeafSubject");

        // Assert: the interface slot is still the root's dictionary, so nothing was taken.
        Assert.NotNull(leaf.Data);
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "NI0064");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenDerivedSubjectDeclaresAPropertyOfTheWrongTypeNamedLikeAnInterfaceMember_ThenNI0064IsNotReported()
    {
        // Arrange: object is not ConcurrentDictionary<(string?, string), object?>, so this is not an
        // implicit implementation and the interface mapping falls back to the root's.
        var source = LeafDeclaring("public object Data { get; } = new object();");

        // Act
        var result = GeneratorTestHost.RunForExecution(source);
        var instance = result.CreateInstance("Repro.LeafSubject");
        var ownData = instance.GetType().GetProperty("Data")!.GetValue(instance);

        // Assert: the two are different objects, which is the proof the slot was not taken.
        Assert.False(ReferenceEquals(((IInterceptorSubject)instance).Data, ownData));
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "NI0064");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenDerivedSubjectDeclaresAMethodWithTheWrongReturnTypeNamedLikeAnInterfaceMember_ThenNI0064IsNotReported()
    {
        // Arrange: the return type is part of what makes an implicit implementation, so a bool
        // returning AddProperties does not take the void returning interface member's slot.
        var source = LeafDeclaring(
            "public bool AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) => true;");

        // Act
        var result = GeneratorTestHost.RunForExecution(source);
        var leaf = (IInterceptorSubject)result.CreateInstance("Repro.LeafSubject");
        leaf.AddProperties(new SubjectPropertyMetadata(
            "Extra", typeof(string), [], _ => "e", (_, _) => { }, isIntercepted: false, isDynamic: true));

        // Assert: the root's implementation still runs, so the property is really added.
        Assert.True(leaf.Properties.ContainsKey("Extra"));
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "NI0064");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenDerivedSubjectDeclaresAPublicSyncRoot_ThenTheInterfaceSlotIsReallyTaken()
    {
        // Arrange: the counterpart of the false positive cases. object SyncRoot { get; } matches the
        // interface member exactly, so it is an implicit implementation and NI0064 is justified.
        var source = LeafDeclaring("public object SyncRoot { get; } = new object();");

        // Act
        var result = GeneratorTestHost.RunForExecution(source);
        var instance = result.CreateInstance("Repro.LeafSubject");
        var ownSyncRoot = instance.GetType().GetProperty("SyncRoot")!.GetValue(instance);

        // Assert
        Assert.Same(ownSyncRoot, ((IInterceptorSubject)instance).SyncRoot);
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0064");
    }

    [Fact]
    public void WhenDerivedSubjectHandWritesTheContextImplementation_ThenNI0064IsReported()
    {
        // Arrange: this compiles with no diagnostic and kills interception entirely, not only on
        // base declared properties, because writes still land in the backing fields.
        var source = LeafDeclaring(
            "IInterceptorSubjectContext IInterceptorSubject.Context { get; } = InterceptorSubjectContext.Create();");

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0064");
    }

    [Fact]
    public void WhenAnIntermediateOverridesAPublicContext_ThenNoDiagnosticIsReportedAndWritesAreIntercepted()
    {
        // Arrange: an override occupies the slot the overridden member already had, so it displaces
        // nothing, and virtual dispatch makes it the implementation rather than a replacement for
        // one. Reporting it was an NI0064, an error, on a hierarchy that builds and intercepts.
        var source = VirtualContextBase + OverridingIntermediateDerived;

        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => writeInterceptor);

        // Act
        var result = GeneratorTestHost.RunForExecution(source);
        var derivedType = result.LoadAssembly().GetType("Repro.GenDerived");
        Assert.NotNull(derivedType);
        var derived = Activator.CreateInstance(derivedType, context)!;
        derivedType.GetProperty("Name")!.SetValue(derived, "n");

        // Assert
        Assert.Contains(writeInterceptor.Writes, w => w.PropertyName == "Name" && Equals(w.Value, "n"));
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "NI0064");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenAReferencedSubjectIsSubclassedByHandWithAPublicContext_ThenNI0064IsReportedAndWritesAreNotIntercepted()
    {
        // Arrange: the hand-written class satisfies the contract by inheriting the referenced
        // subject's interception members, so it declares no explicit implementation of its own and its public
        // Context wins the slot for every generated subclass. That is the silent interception loss
        // in the shape the rule exists to prevent, and it produces no compiler diagnostic at all.
        const string librarySource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Lib
            {
                [InterceptorSubject]
                public partial class LibRoot
                {
                    public partial string A { get; set; }
                }
            }
            """;

        const string mainSource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public class Hand : Lib.LibRoot, IInterceptorSubject
                {
                    public IInterceptorSubjectContext Context { get; } = InterceptorSubjectContext.Create();
                }

                [InterceptorSubject]
                public partial class HandLeaf : Hand
                {
                    public partial string B { get; set; }
                }
            }
            """;

        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => writeInterceptor);

        // Act
        var result = GeneratorTestHost.RunWithLibraryReferenceForExecution(librarySource, mainSource);
        var leafType = result.LoadAssembly().GetType("Repro.HandLeaf");
        Assert.NotNull(leafType);
        var leaf = Activator.CreateInstance(leafType, context)!;
        leafType.GetProperty("B")!.SetValue(leaf, "b");

        // Assert: the write lands in the backing field and the executor never sees it, so the value
        // still looks right. NI0064 is the only signal there is.
        Assert.Equal("b", leafType.GetProperty("B")!.GetValue(leaf));
        Assert.Empty(writeInterceptor.Writes);
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0064");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenBaseImplementsTheContractWithPublicMembers_ThenNoDiagnosticIsReportedAndWritesAreIntercepted()
    {
        // Arrange: the counterpart of the case above and the shape that must not regress. The base
        // implements IInterceptorSubject with plain public members and derives from object, so there
        // is nothing above it whose slot those members could take.
        var source = PublicMemberBase + GeneratedDerived;

        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => writeInterceptor);

        // Act
        var result = GeneratorTestHost.RunForExecution(source);
        var derivedType = result.LoadAssembly().GetType("Repro.GenDerived");
        Assert.NotNull(derivedType);
        var derived = Activator.CreateInstance(derivedType, context)!;
        derivedType.GetProperty("Name")!.SetValue(derived, "n");

        // Assert
        Assert.Contains(writeInterceptor.Writes, w => w.PropertyName == "Name" && Equals(w.Value, "n"));
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id is "NI0007" or "NI0062" or "NI0063" or "NI0064");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenAReferencedSubjectIsSubclassedByHandWithAnExplicitContext_ThenNI0064IsReportedAndWritesAreNotIntercepted()
    {
        // Arrange: the explicit form of the hijack, and the only one a hand-written ancestor can
        // express, because C# requires the class to list the interface itself (CS0540) and listing it
        // is what makes the class the contract provider. Exempting the explicit form therefore
        // exempted every base class there is, which left this shape reported by nothing at all.
        const string librarySource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Lib
            {
                [InterceptorSubject]
                public partial class LibRoot
                {
                    public partial string A { get; set; }
                }
            }
            """;

        const string mainSource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public class Hand : Lib.LibRoot, IInterceptorSubject
                {
                    private readonly IInterceptorSubjectContext _own = InterceptorSubjectContext.Create();

                    IInterceptorSubjectContext IInterceptorSubject.Context => _own;
                }

                [InterceptorSubject]
                public partial class HandLeaf : Hand
                {
                    public partial string B { get; set; }
                }
            }
            """;

        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => writeInterceptor);

        // Act
        var result = GeneratorTestHost.RunWithLibraryReferenceForExecution(librarySource, mainSource);
        var leafType = result.LoadAssembly().GetType("Repro.HandLeaf");
        Assert.NotNull(leafType);
        var leaf = Activator.CreateInstance(leafType, context)!;
        leafType.GetProperty("B")!.SetValue(leaf, "b");

        // Assert: the inherited helpers keep reading the root's field, which nothing populates, so
        // the write lands in the backing field and the value still looks right.
        Assert.Equal("b", leafType.GetProperty("B")!.GetValue(leaf));
        Assert.Empty(writeInterceptor.Writes);
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0064");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenAHijackerSitsAboveTheContractProvider_ThenNI0064IsReportedAndWritesAreNotIntercepted()
    {
        // Arrange: Hand satisfies the contract by inheritance and declares nothing, so it is the
        // contract provider and the scan used to stop there. The public Context that really takes the
        // slot sits one class further up, where nothing ever looked.
        const string librarySource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Lib
            {
                [InterceptorSubject]
                public partial class LibRoot
                {
                    public partial string A { get; set; }
                }
            }
            """;

        const string mainSource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public class Middle : Lib.LibRoot, IInterceptorSubject
                {
                    private readonly IInterceptorSubjectContext _own = InterceptorSubjectContext.Create();

                    public IInterceptorSubjectContext Context => _own;
                }

                public class Hand : Middle, IInterceptorSubject
                {
                }

                [InterceptorSubject]
                public partial class HandLeaf : Hand
                {
                    public partial string B { get; set; }
                }
            }
            """;

        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => writeInterceptor);

        // Act
        var result = GeneratorTestHost.RunWithLibraryReferenceForExecution(librarySource, mainSource);
        var leafType = result.LoadAssembly().GetType("Repro.HandLeaf");
        Assert.NotNull(leafType);
        var leaf = Activator.CreateInstance(leafType, context)!;
        leafType.GetProperty("B")!.SetValue(leaf, "b");

        // Assert: the declarer named in the message is Middle, not the provider below it.
        Assert.Equal("b", leafType.GetProperty("B")!.GetValue(leaf));
        Assert.Empty(writeInterceptor.Writes);
        var diagnostic = Assert.Single(result.GeneratorDiagnostics, d => d.Id == "NI0064");
        Assert.Contains("Repro.Middle", diagnostic.GetMessage());
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    /// <summary>
    /// <see cref="PublicMemberBase"/> with a virtual Context, which is what an intermediate class
    /// needs in order to override it rather than hide it.
    /// </summary>
    private static readonly string VirtualContextBase = PublicMemberBase.Replace(
        "public IInterceptorSubjectContext Context",
        "public virtual IInterceptorSubjectContext Context");

    private const string OverridingIntermediateDerived = """

        namespace Repro
        {
            public class Middle : HandBase
            {
                public override IInterceptorSubjectContext Context => base.Context;
            }

            [Namotion.Interceptor.Attributes.InterceptorSubject]
            public partial class GenDerived : Middle
            {
                public partial string Name { get; set; }
            }
        }
        """;
}
