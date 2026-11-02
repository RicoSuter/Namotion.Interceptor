using Namotion.Interceptor.Tracking;
using Xunit;
using static Namotion.Interceptor.Generator.Tests.SubjectInheritanceTestSources;

namespace Namotion.Interceptor.Generator.Tests;

public class SubjectInterfaceSlotTests
{
    [Fact]
    public void WhenDerivedSubjectDeclaresAPublicData_ThenNI0064IsReported()
    {
        // Arrange: the derived class re-lists the interface, so its public member takes the Data
        // slot from the root's explicit implementation.
        const string source = """
            using System.Collections.Concurrent;
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

                    public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();
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
    public void WhenDerivedSubjectDeclaresAPublicData_ThenTheInterfaceSlotIsReallyTaken()
    {
        // Arrange: the counterpart of the false positive cases. The property matches the interface
        // member exactly, so it is an implicit implementation and NI0064 is justified.
        var source = LeafDeclaring(
            "public System.Collections.Concurrent.ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();");

        // Act
        var result = GeneratorTestHost.RunForExecution(source);
        var instance = result.CreateInstance("Repro.LeafSubject");
        var ownData = instance.GetType().GetProperty("Data")!.GetValue(instance);

        // Assert
        Assert.Same(ownData, ((IInterceptorSubject)instance).Data);
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0064");
    }

    [Fact]
    public void WhenDerivedSubjectHandWritesTheExecutorImplementation_ThenNI0064IsReported()
    {
        // Arrange: this compiles with no diagnostic and kills interception entirely, not only on
        // base declared properties, because writes still land in the backing fields.
        var source = LeafDeclaring(
            "Namotion.Interceptor.Interceptors.IInterceptorExecutor IInterceptorSubject.Executor => null!;");

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0064");
    }

    [Fact]
    public void WhenAnIntermediateOverridesAPublicExecutor_ThenNoDiagnosticIsReportedAndWritesAreIntercepted()
    {
        // Arrange: an override occupies the slot the overridden member already had, so it displaces
        // nothing, and virtual dispatch makes it the implementation rather than a replacement for
        // one. Reporting it was an NI0064, an error, on a hierarchy that builds and intercepts.
        var source = VirtualExecutorBase + OverridingIntermediateDerived;

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
    public void WhenAReferencedSubjectIsSubclassedByHandWithAPublicExecutor_ThenNI0064IsReportedAndWritesAreNotIntercepted()
    {
        // Arrange: the hand-written class satisfies the contract by inheriting the referenced
        // subject's interception members, so it declares no explicit implementation of its own and its public
        // Executor wins the slot for every generated subclass. The root's helpers keep reading the
        // root's own field, which nothing populates through the hijacked slot, so interception is
        // silently lost, and it produces no compiler diagnostic at all.
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
                    private Namotion.Interceptor.Interceptors.IInterceptorExecutor? _own;
                    public Namotion.Interceptor.Interceptors.IInterceptorExecutor Executor
                        => Namotion.Interceptor.Interceptors.InterceptorExecutor.GetOrCreate(ref _own, this);
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
                    private Namotion.Interceptor.Interceptors.IInterceptorExecutor? _own;

                    Namotion.Interceptor.Interceptors.IInterceptorExecutor IInterceptorSubject.Executor
                        => Namotion.Interceptor.Interceptors.InterceptorExecutor.GetOrCreate(ref _own, this);
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
        // contract provider and the scan used to stop there. The public Executor that really takes
        // the slot sits one class further up, where nothing ever looked.
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
                    private Namotion.Interceptor.Interceptors.IInterceptorExecutor? _own;

                    public Namotion.Interceptor.Interceptors.IInterceptorExecutor Executor
                        => Namotion.Interceptor.Interceptors.InterceptorExecutor.GetOrCreate(ref _own, this);
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
    /// <see cref="PublicMemberBase"/> with a virtual Executor, which is what an intermediate class
    /// needs in order to override it rather than hide it.
    /// </summary>
    private static readonly string VirtualExecutorBase = PublicMemberBase.Replace(
        "public IInterceptorExecutor Executor",
        "public virtual IInterceptorExecutor Executor");

    private const string OverridingIntermediateDerived = """

        namespace Repro
        {
            public class Middle : HandBase
            {
                public override IInterceptorExecutor Executor => base.Executor;
            }

            [Namotion.Interceptor.Attributes.InterceptorSubject]
            public partial class GenDerived : Middle
            {
                public partial string Name { get; set; }
            }
        }
        """;
}
