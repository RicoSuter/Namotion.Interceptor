using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

public class SubjectBaseDiagnosticsTests
{
    /// <summary>
    /// A generated root plus a generated leaf, with <c>{0}</c> replaced by the leaf member under
    /// test. Every NI0013 and NI0014 case is this shape with one member swapped.
    /// </summary>
    private const string LeafMemberTemplate = """
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

                {0}
            }
        }
        """;

    private static string LeafDeclaring(string memberDeclaration)
        => LeafMemberTemplate.Replace("{0}", memberDeclaration);

    private const string NonConformingBase = """
        using System;
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Collections.Frozen;
        using System.Linq;
        using Namotion.Interceptor;
        using Namotion.Interceptor.Interceptors;

        namespace Repro
        {
            public class HandBase : IInterceptorSubject
            {
                private IInterceptorExecutor? _context;
                private IReadOnlyDictionary<string, SubjectPropertyMetadata> _properties
                    = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                IInterceptorSubjectContext IInterceptorSubject.Context => InterceptorExecutor.GetOrCreate(ref _context, this);
                ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                object IInterceptorSubject.SyncRoot { get; } = new object();
                IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => _properties;

                public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
                {
                    _properties = _properties
                        .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
                        .ToFrozenDictionary();
                }
            }
        }
        """;

    private const string DefaultPropertiesOnlyBase = """
        using System;
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Collections.Frozen;
        using System.Linq;
        using Namotion.Interceptor;
        using Namotion.Interceptor.Interceptors;

        namespace Repro
        {
            public class HandBase : IInterceptorSubject
            {
                private IInterceptorExecutor? _context;
                private IReadOnlyDictionary<string, SubjectPropertyMetadata> _properties
                    = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; }
                    = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                IInterceptorSubjectContext IInterceptorSubject.Context => InterceptorExecutor.GetOrCreate(ref _context, this);
                ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                object IInterceptorSubject.SyncRoot { get; } = new object();
                IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => _properties;

                public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
                {
                    _properties = _properties
                        .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
                        .ToFrozenDictionary();
                }
            }
        }
        """;

    /// <summary>
    /// Same as <see cref="DefaultPropertiesOnlyBase"/> plus an unrelated overload of one of the four
    /// plumbing names. C# hides a method by signature, so this overload hides nothing the generator
    /// emits and a 'new' modifier on the emitted member would be CS0109.
    /// </summary>
    private const string DifferentSignatureOverloadBase = """
        using System;
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Collections.Frozen;
        using System.Linq;
        using Namotion.Interceptor;
        using Namotion.Interceptor.Interceptors;

        namespace Repro
        {
            public class HandBase : IInterceptorSubject
            {
                private IInterceptorExecutor? _context;
                private IReadOnlyDictionary<string, SubjectPropertyMetadata> _properties
                    = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; }
                    = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                protected int GetInstanceProperties(int unused) => unused;

                IInterceptorSubjectContext IInterceptorSubject.Context => InterceptorExecutor.GetOrCreate(ref _context, this);
                ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                object IInterceptorSubject.SyncRoot { get; } = new object();
                IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => _properties;

                public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
                {
                    _properties = _properties
                        .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
                        .ToFrozenDictionary();
                }
            }
        }
        """;

    /// <summary>
    /// Same as <see cref="DefaultPropertiesOnlyBase"/> plus a GetInstanceProperties whose signature
    /// does match the emitted one, so the emitted member hides it and needs the 'new' modifier.
    /// </summary>
    private const string MatchingSignatureMemberBase = """
        using System;
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Collections.Frozen;
        using System.Linq;
        using Namotion.Interceptor;
        using Namotion.Interceptor.Interceptors;

        namespace Repro
        {
            public class HandBase : IInterceptorSubject
            {
                private IInterceptorExecutor? _context;
                private IReadOnlyDictionary<string, SubjectPropertyMetadata> _properties
                    = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; }
                    = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                protected IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties() => _properties;

                IInterceptorSubjectContext IInterceptorSubject.Context => InterceptorExecutor.GetOrCreate(ref _context, this);
                ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                object IInterceptorSubject.SyncRoot { get; } = new object();
                IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => _properties;

                public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
                {
                    _properties = _properties
                        .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
                        .ToFrozenDictionary();
                }
            }
        }
        """;

    /// <summary>
    /// A DefaultProperties whose display string mentions SubjectPropertyMetadata but which the
    /// emitted .Concat(...) cannot consume.
    /// </summary>
    private const string WronglyTypedDefaultPropertiesBase = """
        using System;
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Collections.Frozen;
        using System.Linq;
        using Namotion.Interceptor;
        using Namotion.Interceptor.Interceptors;

        namespace Repro
        {
            public class HandBase : IInterceptorSubject
            {
                private IInterceptorExecutor? _context;
                private IReadOnlyDictionary<string, SubjectPropertyMetadata> _properties
                    = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                public static IReadOnlyList<SubjectPropertyMetadata> DefaultProperties { get; }
                    = new List<SubjectPropertyMetadata>();

                IInterceptorSubjectContext IInterceptorSubject.Context => InterceptorExecutor.GetOrCreate(ref _context, this);
                ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                object IInterceptorSubject.SyncRoot { get; } = new object();
                IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => _properties;

                public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
                {
                    _properties = _properties
                        .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
                        .ToFrozenDictionary();
                }
            }
        }
        """;

    /// <summary>
    /// The same correctly typed DefaultProperties as <see cref="DefaultPropertiesOnlyBase"/>, but
    /// declared as a field. This shape compiles today, so it must not become an error.
    /// </summary>
    private const string DefaultPropertiesFieldBase = """
        using System;
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Collections.Frozen;
        using System.Linq;
        using Namotion.Interceptor;
        using Namotion.Interceptor.Interceptors;

        namespace Repro
        {
            public class HandBase : IInterceptorSubject
            {
                private IInterceptorExecutor? _context;
                private IReadOnlyDictionary<string, SubjectPropertyMetadata> _properties
                    = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                public static readonly IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties
                    = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                IInterceptorSubjectContext IInterceptorSubject.Context => InterceptorExecutor.GetOrCreate(ref _context, this);
                ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                object IInterceptorSubject.SyncRoot { get; } = new object();
                IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => _properties;

                public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
                {
                    _properties = _properties
                        .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
                        .ToFrozenDictionary();
                }
            }
        }
        """;

    private const string GeneratedDerived = """

        namespace Repro
        {
            [Namotion.Interceptor.Attributes.InterceptorSubject]
            public partial class GenDerived : HandBase
            {
                public partial string Name { get; set; }
            }
        }
        """;

    [Fact]
    public void WhenBaseImplementsTheInterfaceWithoutTheContract_ThenNI0011IsReported()
    {
        // Arrange: no DefaultProperties, no helpers. Today this shape dies on CS0117 inside
        // generated code, which the user cannot edit.
        var source = NonConformingBase + GeneratedDerived;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0011");
        Assert.DoesNotContain(result.CompilationErrors, d => d.Id == "CS0117");
    }

    [Fact]
    public void WhenBaseHasOnlyDefaultProperties_ThenNI0012IsReportedAndItStillCompiles()
    {
        // Arrange: this shape compiles and works today, so it must not become an error.
        var source = DefaultPropertiesOnlyBase + GeneratedDerived;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert: warning, root-mode fallback, and no stray 'new' (which would be CS0109).
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0012");
        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain(result.CompilationWarnings, d => d.Id == "CS0109");
    }

    [Fact]
    public void WhenBaseDeclaresADifferentSignatureOverloadOfAPlumbingName_ThenNoStrayNewModifierIsEmitted()
    {
        // Arrange: the base takes the NI0012 root-mode fallback and declares GetInstanceProperties(int),
        // which hides nothing because C# hides methods by signature.
        var source = DifferentSignatureOverloadBase + GeneratedDerived;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0012");
        Assert.DoesNotContain(result.CompilationWarnings, d => d.Id == "CS0109");
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void WhenBaseDeclaresAMatchingSignatureOfAPlumbingName_ThenTheNewModifierIsStillEmitted()
    {
        // Arrange: the counterpart of the overload case. Narrowing the hiding check to a signature
        // match must not stop the modifier being emitted where C# does require it (CS0108).
        var source = MatchingSignatureMemberBase + GeneratedDerived;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0012");
        Assert.Contains("new protected IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties()", result.AllSources());
        Assert.DoesNotContain(result.CompilationWarnings, d => d.Id is "CS0108" or "CS0109");
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void WhenBaseDeclaresDefaultPropertiesOfAnUnusableType_ThenNI0011IsReported()
    {
        // Arrange: IReadOnlyList<SubjectPropertyMetadata> mentions the metadata type but the emitted
        // .Concat(...) cannot consume it, which is the CS1929 the contract check exists to replace.
        var source = WronglyTypedDefaultPropertiesBase + GeneratedDerived;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0011");
        Assert.DoesNotContain(result.CompilationErrors, d => d.Id == "CS1929");
    }

    [Fact]
    public void WhenBaseDeclaresDefaultPropertiesAsAField_ThenItIsAcceptedAndNI0012IsReported()
    {
        // Arrange: a static readonly field of the right type is usable by the emitted .Concat(...)
        // exactly like a property, so it must not be rejected outright.
        var source = DefaultPropertiesFieldBase + GeneratedDerived;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "NI0011");
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0012");
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void WhenAttributedAncestorIsNotPartial_ThenTheDerivedSubjectDoesNotAssumeGeneratedPlumbing()
    {
        // Arrange: the ancestor carries the attribute but NI0001 suppresses its generation, so none
        // of the plumbing the derived class would inherit ever exists.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public class NonPartialBase
                {
                }

                [InterceptorSubject]
                public partial class GenDerived : NonPartialBase
                {
                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert: one actionable diagnostic per class instead of a wall of raw errors in code the
        // user cannot edit.
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0001");
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0011");
        Assert.DoesNotContain("GetPropertyValue", result.AllSources());
        Assert.DoesNotContain("NonPartialBase.DefaultProperties", result.AllSources());
        Assert.DoesNotContain(result.CompilationErrors, d => d.Id is "CS0535" or "CS0117" or "CS0103");
    }

    [Fact]
    public void WhenDerivedSubjectDeclaresAGeneratedMemberName_ThenNI0013IsReported()
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

        // Assert: the capture is invisible to the compiler, so NI0013 is the only signal there is.
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0013");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenDerivedSubjectDeclaresAPublicSyncRoot_ThenNI0014IsReported()
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
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0014");
    }

    [Fact]
    public void WhenRootSubjectDeclaresAPublicSyncRoot_ThenNoDiagnosticIsReported()
    {
        // Arrange: interface mapping prefers a class's own explicit implementation over its own
        // public members, so the root is never hijacked by its own member.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class RootSubject
                {
                    public partial string RootName { get; set; }

                    public object SyncRoot { get; } = new object();
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id is "NI0013" or "NI0014");
    }

    [Fact]
    public void WhenAHandWrittenSubjectSitsBetweenTwoGeneratedSubjects_ThenTheLeafFallsBackToRootMode()
    {
        // Arrange: the middle re-implements IInterceptorSubject by hand, so its Context wins the
        // interface map while the root's helpers still read the root's never-populated field.
        // Selecting derived mode here would reproduce the bug this whole change fixes.
        const string source = """
            using System;
            using System.Collections.Concurrent;
            using System.Collections.Generic;
            using System.Collections.Frozen;
            using System.Linq;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Interceptors;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class GenRoot
                {
                    public partial string RootName { get; set; }
                }

                public class HandMiddle : GenRoot, IInterceptorSubject
                {
                    private IInterceptorExecutor? _context;

                    // Present so the leaf reaches mode selection at all: without it the middle fails
                    // the contract outright and NI0011 suppresses the leaf's generation, which never
                    // exercises the choice between root and derived mode.
                    public static new IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; }
                        = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                    IInterceptorSubjectContext IInterceptorSubject.Context => InterceptorExecutor.GetOrCreate(ref _context, this);
                    ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                    object IInterceptorSubject.SyncRoot { get; } = new object();
                    IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties
                        => FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                    void IInterceptorSubject.AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) { }
                }

                [InterceptorSubject]
                public partial class GenLeaf : HandMiddle
                {
                    public partial string LeafName { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);
        var leaf = Assert.Single(result.Sources, s => s.HintName.Contains("Repro.GenLeaf.g.cs")).SourceText.ToString();

        // Assert: root mode, so the leaf owns its own executor rather than reading one nothing fills.
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id is "NI0011" or "NI0012");
        Assert.Contains("private IInterceptorExecutor? _context;", leaf);

        // Everything the leaf re-emits hides a member of the generated root, and every one of those
        // is a CS0108 in a file the consumer cannot edit, which their TreatWarningsAsErrors turns
        // into a build failure. The two INPC members are asserted by name because they are gated by
        // BaseClassHasInpc rather than by the symbol lookup the four helpers go through.
        Assert.Contains("new public event PropertyChangedEventHandler? PropertyChanged;", leaf);
        Assert.Contains("new protected void RaisePropertyChanged(string propertyName)", leaf);
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenAnIntermediateClassDeclaresAPrivateGeneratedMemberName_ThenNI0013IsNotReported()
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
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "NI0013");
    }

    [Fact]
    public void WhenBaseDefaultPropertiesHasTheWrongType_ThenNI0011IsReportedRatherThanACompilerError()
    {
        // Arrange: goal 5. Accepting any static named DefaultProperties lets this through and the
        // generated .Concat(...) then fails with CS1929 inside code the user cannot edit.
        const string source = """
            using System;
            using System.Collections.Concurrent;
            using System.Collections.Generic;
            using System.Collections.Frozen;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Interceptors;

            namespace Repro
            {
                public class HandBase : IInterceptorSubject
                {
                    private IInterceptorExecutor? _context;

                    public static int DefaultProperties { get; } = 0;

                    IInterceptorSubjectContext IInterceptorSubject.Context => InterceptorExecutor.GetOrCreate(ref _context, this);
                    ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                    object IInterceptorSubject.SyncRoot { get; } = new object();
                    IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties
                        => FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                    void IInterceptorSubject.AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) { }
                }

                [InterceptorSubject]
                public partial class GenDerived : HandBase
                {
                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0011");
        Assert.DoesNotContain(result.CompilationErrors, d => d.Id == "CS1929");
    }

    [Fact]
    public void WhenDerivedSubjectDeclaresAStaticGeneratedMemberName_ThenNI0013IsReportedAndTheCallIsCaptured()
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

        // Assert: the added property is swallowed, and NI0013 is the only signal there is.
        Assert.False(leaf.Properties.ContainsKey("Extra"));
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0013");
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
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id is "NI0013" or "NI0014");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenDerivedSubjectDeclaresAPartialPropertyNamedLikeAnInterfaceMember_ThenNI0014IsNotReported()
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
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "NI0014");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenDerivedSubjectDeclaresAPropertyOfTheWrongTypeNamedLikeAnInterfaceMember_ThenNI0014IsNotReported()
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
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "NI0014");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenDerivedSubjectDeclaresAMethodWithTheWrongReturnTypeNamedLikeAnInterfaceMember_ThenNI0014IsNotReported()
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
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "NI0014");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenDerivedSubjectDeclaresAPublicSyncRoot_ThenTheInterfaceSlotIsReallyTaken()
    {
        // Arrange: the counterpart of the false positive cases. object SyncRoot { get; } matches the
        // interface member exactly, so it is an implicit implementation and NI0014 is justified.
        var source = LeafDeclaring("public object SyncRoot { get; } = new object();");

        // Act
        var result = GeneratorTestHost.RunForExecution(source);
        var instance = result.CreateInstance("Repro.LeafSubject");
        var ownSyncRoot = instance.GetType().GetProperty("SyncRoot")!.GetValue(instance);

        // Assert
        Assert.Same(ownSyncRoot, ((IInterceptorSubject)instance).SyncRoot);
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0014");
    }

    [Fact]
    public void WhenDerivedSubjectHandWritesTheContextImplementation_ThenNI0014IsReported()
    {
        // Arrange: this compiles with no diagnostic and kills interception entirely, not only on
        // base declared properties, because writes still land in the backing fields.
        var source = LeafDeclaring(
            "IInterceptorSubjectContext IInterceptorSubject.Context { get; } = InterceptorSubjectContext.Create();");

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0014");
    }
}
