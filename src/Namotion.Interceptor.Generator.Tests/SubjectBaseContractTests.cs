using Namotion.Interceptor.Tracking;
using Xunit;
using static Namotion.Interceptor.Generator.Tests.SubjectInheritanceTestSources;

namespace Namotion.Interceptor.Generator.Tests;

public class SubjectBaseContractTests
{
    [Fact]
    public void WhenBaseImplementsTheInterfaceWithoutTheContract_ThenNI0007IsReported()
    {
        // Arrange: no DefaultProperties, no helpers. Today this shape dies on CS0117 inside
        // generated code, which the user cannot edit.
        var source = NonConformingBase + GeneratedDerived;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0007");
        Assert.DoesNotContain(result.CompilationErrors, d => d.Id == "CS0117");
    }

    [Fact]
    public void WhenBaseHasOnlyDefaultProperties_ThenNI0062IsReportedAndItStillCompiles()
    {
        // Arrange: this shape compiles and works today, so it must not become an error.
        var source = DefaultPropertiesOnlyBase + GeneratedDerived;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert: warning, root-mode fallback, and no modifier mistake in either direction. Both
        // CS0108 and CS0109 are warnings, so naming one of them lets the other through, and a
        // consumer's TreatWarningsAsErrors fails on either.
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0062");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenBaseDeclaresADifferentSignatureOverloadOfAnInterceptionMemberName_ThenNoStrayNewModifierIsEmitted()
    {
        // Arrange: the base takes the NI0062 root-mode fallback and declares GetInstanceProperties(int),
        // which hides nothing because C# hides methods by signature.
        var source = DifferentSignatureOverloadBase + GeneratedDerived;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert: no stray 'new' on the emitted members, and no missing one either. CS0108 is a
        // warning like CS0109, so asserting the absence of one alone would pass with the other.
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0062");
        Assert.Empty(result.CompilationWarnings);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void WhenBaseDeclaresAMatchingSignatureOfAnInterceptionMemberName_ThenTheNewModifierIsStillEmitted()
    {
        // Arrange: the counterpart of the overload case. Narrowing the hiding check to a signature
        // match must not stop the modifier being emitted where C# does require it (CS0108).
        var source = MatchingSignatureMemberBase + GeneratedDerived;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0062");
        Assert.Contains("new protected IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties()", result.AllSources());
        Assert.DoesNotContain(result.CompilationWarnings, d => d.Id is "CS0108" or "CS0109");
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void WhenBaseDeclaresDefaultPropertiesOfAnUnusableType_ThenNI0007IsReported()
    {
        // Arrange: IReadOnlyList<SubjectPropertyMetadata> mentions the metadata type but the emitted
        // .Concat(...) cannot consume it, which is the CS1929 the contract check exists to replace.
        var source = WronglyTypedDefaultPropertiesBase + GeneratedDerived;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0007");
        Assert.DoesNotContain(result.CompilationErrors, d => d.Id == "CS1929");
    }

    [Fact]
    public void WhenBaseDeclaresDefaultPropertiesAsAField_ThenItIsAcceptedAndNI0062IsReported()
    {
        // Arrange: a static readonly field of the right type is usable by the emitted .Concat(...)
        // exactly like a property, so it must not be rejected outright.
        var source = DefaultPropertiesFieldBase + GeneratedDerived;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "NI0007");
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0062");
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void WhenAttributedAncestorIsNotPartial_ThenTheDerivedSubjectDoesNotAssumeGeneratedInterceptionMembers()
    {
        // Arrange: the ancestor carries the attribute but NI0001 suppresses its generation, so none
        // of the members the derived class would inherit ever exists.
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
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0007");
        Assert.DoesNotContain("GetPropertyValue", result.AllSources());
        Assert.DoesNotContain("NonPartialBase.DefaultProperties", result.AllSources());
        Assert.DoesNotContain(result.CompilationErrors, d => d.Id is "CS0535" or "CS0117" or "CS0103");
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
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id is "NI0063" or "NI0064");
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
                    // the contract outright and NI0007 suppresses the leaf's generation, which never
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
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id is "NI0007" or "NI0062");
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
    public void WhenBaseDefaultPropertiesHasTheWrongType_ThenNI0007IsReportedRatherThanACompilerError()
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
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0007");
        Assert.DoesNotContain(result.CompilationErrors, d => d.Id == "CS1929");
    }

    [Fact]
    public void WhenBaseDeclaresAnAccessorHelperWithTheWrongReturnType_ThenTheContractRejectsIt()
    {
        // Arrange: every accessor helper is present and only SetPropertyValue returns void. The
        // generated setter tests that return value in "!cancel && SetPropertyValue(...)", so
        // accepting this base means CS0019 inside a generated file, which is exactly the outcome
        // the contract check exists to replace.
        var source = WrongReturnTypeBase + GeneratedDerived;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert: rejected by the contract, so the subject falls back to its own interception members (NI0062)
        // instead of calling the base helper that does not fit. The message names the one member
        // that failed, which is what tells this base apart from the four other defects that reach
        // the same rule.
        var diagnostic = Assert.Single(result.GeneratorDiagnostics, d => d.Id == "NI0062");
        Assert.Contains("bool SetPropertyValue", diagnostic.GetMessage());
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenBaseGetInstancePropertiesReturnsAnImplementingType_ThenTheContractAcceptsIt()
    {
        // Arrange: the base returns FrozenDictionary<string, SubjectPropertyMetadata>?, which the
        // emitted "GetInstanceProperties() ?? DefaultProperties" consumes exactly like the interface.
        var source = ImplementingInstancePropertiesBase + GeneratedDerived;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert: derived mode. Comparing the return type by identity sent this base to the NI0062
        // root-mode fallback, which costs the base's own properties their interception, the very
        // failure the shared interception members exist to fix.
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id is "NI0007" or "NI0062");
        Assert.DoesNotContain("private IInterceptorExecutor? _context;", result.AllSources());
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenBaseGetInstancePropertiesReturnsAValueType_ThenTheContractRejectsIt()
    {
        // Arrange: the counterpart of the FrozenDictionary case. Widening the return type to any
        // implementer of the dictionary interface also admitted a struct, and the emitted
        // "GetInstanceProperties() ?? DefaultProperties" then fails with CS0019 in a file the
        // consumer cannot edit, where the narrower rule gave a clean NI0062 fallback.
        var source = ValueTypeInstancePropertiesBase + GeneratedDerived;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert: the compilation is checked first, because CS0019 in generated code is the damage
        // and the diagnostic is only the replacement for it.
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        var diagnostic = Assert.Single(result.GeneratorDiagnostics, d => d.Id == "NI0062");
        Assert.Contains("GetInstanceProperties", diagnostic.GetMessage());
    }

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
    /// interception member names. C# hides a method by signature, so this overload hides nothing the generator
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

    /// <summary>
    /// A base that satisfies every clause of the contract except one: SetPropertyValue returns void
    /// where the generated setter needs a bool. This is the single-typo shape a hand-written base
    /// realistically has, and the name, arity and parameter count all still match.
    /// </summary>
    private const string WrongReturnTypeBase = """
        using System;
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Collections.Frozen;
        using System.ComponentModel;
        using System.Linq;
        using Namotion.Interceptor;
        using Namotion.Interceptor.Interceptors;

        namespace Repro
        {
            public class HandBase : IInterceptorSubject, INotifyPropertyChanged, IRaisePropertyChanged
            {
                private IInterceptorExecutor? _context;
                private IReadOnlyDictionary<string, SubjectPropertyMetadata>? _properties;

                public event PropertyChangedEventHandler? PropertyChanged;

                void IRaisePropertyChanged.RaisePropertyChanged(string propertyName)
                    => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

                IInterceptorSubjectContext IInterceptorSubject.Context => InterceptorExecutor.GetOrCreate(ref _context, this);
                ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                object IInterceptorSubject.SyncRoot { get; } = new object();
                IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => GetInstanceProperties() ?? DefaultProperties;

                void IInterceptorSubject.AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
                    => _properties = ((IInterceptorSubject)this).Properties
                        .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
                        .ToFrozenDictionary();

                public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; }
                    = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                protected IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties() => _properties;

                protected TProperty GetPropertyValue<TProperty>(string propertyName, Func<IInterceptorSubject, TProperty> readValue)
                    => _context is not null ? _context.GetPropertyValue(propertyName, readValue)! : readValue(this)!;

                protected void SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> setValue)
                {
                    if (_context is null)
                    {
                        setValue(this, newValue);
                        return;
                    }

                    _context.SetPropertyValue(propertyName, newValue, currentValue, setValue);
                }

                protected object? InvokeMethod(string methodName, Func<IInterceptorSubject, object?[], object?> invokeMethod, params object?[] parameters)
                    => _context is not null ? _context.InvokeMethod(methodName, parameters, invokeMethod) : invokeMethod(this, parameters);
            }
        }
        """;

    /// <summary>
    /// A base that satisfies every clause of the contract and returns the FrozenDictionary it holds
    /// from GetInstanceProperties rather than the declared interface. The emitted
    /// "GetInstanceProperties() ?? DefaultProperties" consumes that just as happily, which is why
    /// the DefaultProperties half of the same expression has always accepted both forms.
    /// </summary>
    private const string ImplementingInstancePropertiesBase = """
        using System;
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Collections.Frozen;
        using System.ComponentModel;
        using System.Linq;
        using Namotion.Interceptor;
        using Namotion.Interceptor.Interceptors;

        namespace Repro
        {
            public class HandBase : IInterceptorSubject, INotifyPropertyChanged, IRaisePropertyChanged
            {
                private IInterceptorExecutor? _context;
                private FrozenDictionary<string, SubjectPropertyMetadata>? _properties;

                public event PropertyChangedEventHandler? PropertyChanged;

                void IRaisePropertyChanged.RaisePropertyChanged(string propertyName)
                    => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

                IInterceptorSubjectContext IInterceptorSubject.Context => InterceptorExecutor.GetOrCreate(ref _context, this);
                ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                object IInterceptorSubject.SyncRoot { get; } = new object();
                IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => GetInstanceProperties() ?? DefaultProperties;

                void IInterceptorSubject.AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
                    => _properties = ((IInterceptorSubject)this).Properties
                        .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
                        .ToFrozenDictionary();

                public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; }
                    = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                protected FrozenDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties() => _properties;

                protected TProperty GetPropertyValue<TProperty>(string propertyName, Func<IInterceptorSubject, TProperty> readValue)
                    => _context is not null ? _context.GetPropertyValue(propertyName, readValue)! : readValue(this)!;

                protected bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> setValue)
                {
                    if (_context is null)
                    {
                        setValue(this, newValue);
                        return true;
                    }

                    return _context.SetPropertyValue(propertyName, newValue, currentValue, setValue);
                }

                protected object? InvokeMethod(string methodName, Func<IInterceptorSubject, object?[], object?> invokeMethod, params object?[] parameters)
                    => _context is not null ? _context.InvokeMethod(methodName, parameters, invokeMethod) : invokeMethod(this, parameters);
            }
        }
        """;

    /// <summary>
    /// A base that satisfies every clause of the contract except that GetInstanceProperties returns a
    /// struct implementing the dictionary interface. The emitted
    /// "GetInstanceProperties() ?? DefaultProperties" rejects a value type as its left operand with
    /// CS0019, so accepting this base puts a raw compiler error into a generated file.
    /// </summary>
    private const string ValueTypeInstancePropertiesBase = """
        using System;
        using System.Collections;
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Collections.Frozen;
        using System.ComponentModel;
        using System.Linq;
        using Namotion.Interceptor;
        using Namotion.Interceptor.Interceptors;

        namespace Repro
        {
            public readonly struct PropertyMap : IReadOnlyDictionary<string, SubjectPropertyMetadata>
            {
                public SubjectPropertyMetadata this[string key] => throw new KeyNotFoundException();
                public IEnumerable<string> Keys => Array.Empty<string>();
                public IEnumerable<SubjectPropertyMetadata> Values => Array.Empty<SubjectPropertyMetadata>();
                public int Count => 0;
                public bool ContainsKey(string key) => false;

                public bool TryGetValue(string key, out SubjectPropertyMetadata value)
                {
                    value = default;
                    return false;
                }

                public IEnumerator<KeyValuePair<string, SubjectPropertyMetadata>> GetEnumerator()
                    => Enumerable.Empty<KeyValuePair<string, SubjectPropertyMetadata>>().GetEnumerator();

                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            }

            public class HandBase : IInterceptorSubject, INotifyPropertyChanged, IRaisePropertyChanged
            {
                private IInterceptorExecutor? _context;
                private IReadOnlyDictionary<string, SubjectPropertyMetadata>? _properties;

                public event PropertyChangedEventHandler? PropertyChanged;

                void IRaisePropertyChanged.RaisePropertyChanged(string propertyName)
                    => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

                IInterceptorSubjectContext IInterceptorSubject.Context => InterceptorExecutor.GetOrCreate(ref _context, this);
                ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                object IInterceptorSubject.SyncRoot { get; } = new object();
                IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => _properties ?? DefaultProperties;

                void IInterceptorSubject.AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
                    => _properties = ((IInterceptorSubject)this).Properties
                        .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
                        .ToFrozenDictionary();

                public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; }
                    = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                protected PropertyMap GetInstanceProperties() => default;

                protected TProperty GetPropertyValue<TProperty>(string propertyName, Func<IInterceptorSubject, TProperty> readValue)
                    => _context is not null ? _context.GetPropertyValue(propertyName, readValue)! : readValue(this)!;

                protected bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> setValue)
                {
                    if (_context is null)
                    {
                        setValue(this, newValue);
                        return true;
                    }

                    return _context.SetPropertyValue(propertyName, newValue, currentValue, setValue);
                }

                protected object? InvokeMethod(string methodName, Func<IInterceptorSubject, object?[], object?> invokeMethod, params object?[] parameters)
                    => _context is not null ? _context.InvokeMethod(methodName, parameters, invokeMethod) : invokeMethod(this, parameters);
            }
        }
        """;
}
