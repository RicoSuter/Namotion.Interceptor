using System.Reflection;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

public class SubjectBaseInterceptionTests
{
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
    public void WhenSubjectIsSealedAndIsARoot_ThenProtectedMembersAreEmittedPrivate()
    {
        // Arrange: a sealed root emits protected RaisePropertyChanged today, which is CS0628 and
        // therefore a build error for consumers.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public sealed partial class SealedRoot
                {
                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);
        var generated = result.SingleSource();

        // Assert
        Assert.Contains("private void RaisePropertyChanged(string propertyName)", generated);
        Assert.DoesNotContain("protected void RaisePropertyChanged(string propertyName)", generated);
        Assert.Contains("void IRaisePropertyChanged.RaisePropertyChanged(string propertyName)", generated);
    }

    [Fact]
    public void WhenSubclassIsHandWritten_ThenItCanUseTheProtectedHelpers()
    {
        // Arrange: a hand-written subclass must be able to call the generated base's protected helpers.
        const string source = """
            using System;
            using System.Collections.Generic;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class GenBase
                {
                    public partial string BaseName { get; set; }
                }

                public class HandDerived : GenBase
                {
                    private string _own = "";

                    public HandDerived()
                    {
                        // Must run before the first intercepted write: PropertyReference.Metadata
                        // throws when the name is not registered.
                        ((IInterceptorSubject)this).AddProperties(
                            new SubjectPropertyMetadata(
                                nameof(Own),
                                typeof(string),
                                [],
                                o => ((HandDerived)o).Own,
                                (o, v) => ((HandDerived)o).Own = (string)v!,
                                isIntercepted: true,
                                isDynamic: false));
                    }

                    public string Own
                    {
                        get => GetPropertyValue(nameof(Own), static o => ((HandDerived)o)._own);
                        set => SetPropertyValue(nameof(Own), value, static o => ((HandDerived)o)._own, static (o, v) => ((HandDerived)o)._own = v);
                    }

                    // The other two of the four members the design promises a hand-written subclass
                    // can reach.
                    public bool HasAddedProperties => GetInstanceProperties() is not null;

                    public string Describe(string prefix)
                        => (string)InvokeMethod(
                            nameof(Describe),
                            static (s, p) => (string)p[0]! + ((HandDerived)s)._own,
                            prefix)!;
                }
            }
            """;

        // Act & Assert
        GeneratorTestHost.RunExpectingNoWarnings(source);
    }

    [Fact]
    public void WhenHandWrittenSubclassWritesThroughTheHelpers_ThenTheInheritedExecutorInterceptsIt()
    {
        // Arrange: a hand-written subclass must register metadata before its first intercepted write.
        // The base constructor has already published the executor, so missing metadata would throw.
        const string source = """
            using System;
            using System.Collections.Generic;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class GenBase
                {
                    public partial string BaseName { get; set; }
                }

                public class HandDerived : GenBase
                {
                    public const string WrittenInConstructor = "written-in-constructor";

                    private string _own = "";

                    public HandDerived(IInterceptorSubjectContext context) : base(context)
                    {
                        ((IInterceptorSubject)this).AddProperties(
                            new SubjectPropertyMetadata(
                                nameof(Own),
                                typeof(string),
                                [],
                                o => ((HandDerived)o).Own,
                                (o, v) => ((HandDerived)o).Own = (string)v!,
                                isIntercepted: true,
                                isDynamic: false));

                        Own = WrittenInConstructor;
                    }

                    public string Own
                    {
                        get => GetPropertyValue(nameof(Own), static o => ((HandDerived)o)._own);
                        set => SetPropertyValue(nameof(Own), value, static o => ((HandDerived)o)._own, static (o, v) => ((HandDerived)o)._own = v);
                    }
                }
            }
            """;

        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => writeInterceptor);

        var subjectType = GeneratorTestHost.RunForExecution(source).LoadAssembly().GetType("Repro.HandDerived");
        Assert.NotNull(subjectType);
        var ownProperty = subjectType.GetProperty("Own");
        var baseNameProperty = subjectType.GetProperty("BaseName");
        Assert.NotNull(ownProperty);
        Assert.NotNull(baseNameProperty);

        // Act
        var subject = Activator.CreateInstance(subjectType, context);
        ownProperty.SetValue(subject, "written-after-construction");
        baseNameProperty.SetValue(subject, "base-written");

        // Assert: the hand-written property and the generated base property both reach the one
        // executor the root published.
        Assert.Equal("written-after-construction", ownProperty.GetValue(subject));
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "Own" && Equals(write.Value, "written-in-constructor"));
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "Own" && Equals(write.Value, "written-after-construction"));
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "BaseName" && Equals(write.Value, "base-written"));
    }

    [Fact]
    public void WhenTheDocumentedHandWrittenBaseHostsAGeneratedSubclass_ThenItsWritesReachTheInterceptor()
    {
        // Arrange: read the fixture from docs/generator.md so the documented and tested contracts cannot drift.
        var source = ReadDocumentedConformingBaseFixture();

        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => writeInterceptor);

        var result = GeneratorTestHost.RunForExecution(source);
        var machineType = result.LoadAssembly().GetType("Machine");
        Assert.NotNull(machineType);
        var machine = (IInterceptorSubject)Activator.CreateInstance(machineType, context)!;

        // Act
        machineType.GetProperty("SerialNumber")!.SetValue(machine, "serial-written");

        // Assert: the setter uses the base executor. One context field and no hiding warnings
        // ensure the subclass did not emit a second set of interception members.
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "SerialNumber" && Equals(write.Value, "serial-written"));
        Assert.Contains("SerialNumber", machine.Properties.Keys);
        Assert.Equal(1, CountExecutorFields(machineType));
        Assert.DoesNotContain(result.GeneratorDiagnostics, diagnostic => diagnostic.Id is "NI0007" or "NI0062");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenReferencedBaseHasPrivateHelpers_ThenItFallsBackToRootModeWithNI0062()
    {
        // Arrange: simulate an older generated base with private helpers and no GetInstanceProperties.
        // Do not run the generator over the library; this exercises fallback for an incompatible metadata contract.
        const string librarySource = """
            using System;
            using System.Collections.Concurrent;
            using System.Collections.Generic;
            using System.Collections.Frozen;
            using System.ComponentModel;
            using System.Linq;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Interceptors;

            namespace Library
            {
                [InterceptorSubject]
                public class StaleBase : IInterceptorSubject, INotifyPropertyChanged, IRaisePropertyChanged
                {
                    private IInterceptorExecutor? _context;
                    private IReadOnlyDictionary<string, SubjectPropertyMetadata>? _properties;

                    // The old generator paired its private helpers with a protected RaisePropertyChanged,
                    // so the fixture carries both: the attribute alone makes the subclass treat the base
                    // as the INPC owner and call RaisePropertyChanged by simple name.
                    public event PropertyChangedEventHandler? PropertyChanged;

                    protected void RaisePropertyChanged(string propertyName)
                        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

                    void IRaisePropertyChanged.RaisePropertyChanged(string propertyName) => RaisePropertyChanged(propertyName);

                    IInterceptorSubjectContext IInterceptorSubject.Context => InterceptorExecutor.GetOrCreate(ref _context, this);
                    ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                    object IInterceptorSubject.SyncRoot { get; } = new object();
                    IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => _properties ?? DefaultProperties;

                    void IInterceptorSubject.AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
                        => _properties = (_properties ?? DefaultProperties)
                            .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
                            .ToFrozenDictionary();

                    public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; }
                        = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                    private TProperty GetPropertyValue<TProperty>(string propertyName, Func<IInterceptorSubject, TProperty> readValue)
                        => _context is not null ? _context.GetPropertyValue(propertyName, readValue)! : readValue(this)!;

                    private bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, Func<IInterceptorSubject, TProperty> readValue, Action<IInterceptorSubject, TProperty> setValue)
                    {
                        if (_context is null)
                        {
                            setValue(this, newValue);
                            return true;
                        }

                        return _context.SetPropertyValue(propertyName, newValue, readValue, setValue);
                    }

                    private object? InvokeMethod(string methodName, Func<IInterceptorSubject, object?[], object?> invokeMethod, params object?[] parameters)
                        => _context is not null ? _context.InvokeMethod(methodName, parameters, invokeMethod) : invokeMethod(this, parameters);
                }
            }
            """;

        const string mainSource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace App
            {
                [InterceptorSubject]
                public partial class AppLeaf : Library.StaleBase
                {
                    public partial string LeafName { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunWithLibraryReference(librarySource, mainSource);

        // Assert: warning, root mode, still compiles, and no stray 'new' that would be CS0109.
        // The private base helpers neither hide nor bind across the assembly boundary, so the
        // warning check is what pins the modifier decision: CS0109 is a warning, not an error.
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0062");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        Assert.Contains("private IInterceptorExecutor? _context;", result.SingleSource());
    }

    [Fact]
    public void WhenHandWrittenBaseIsGeneric_ThenTheContractIsCheckedWithTypeArgumentsSubstituted()
    {
        // Arrange: DefaultProperties matches the contract only after substituting SubjectPropertyMetadata for T.
        // Looking up members on the open generic definition would incorrectly reject this base.
        const string source = """
            using System;
            using System.Collections.Concurrent;
            using System.Collections.Generic;
            using System.Collections.Frozen;
            using System.ComponentModel;
            using System.Linq;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Interceptors;

            namespace Repro
            {
                public class GenericBase<T> : IInterceptorSubject, INotifyPropertyChanged, IRaisePropertyChanged
                {
                    private IInterceptorExecutor? _context;
                    private IReadOnlyDictionary<string, SubjectPropertyMetadata>? _properties;

                    public event PropertyChangedEventHandler? PropertyChanged;
                    public void RaisePropertyChanged(string propertyName)
                        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

                    public static IReadOnlyDictionary<string, T> DefaultProperties { get; }
                        = FrozenDictionary<string, T>.Empty;

                    IInterceptorSubjectContext IInterceptorSubject.Context => InterceptorExecutor.GetOrCreate(ref _context, this);
                    ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                    object IInterceptorSubject.SyncRoot { get; } = new object();
                    IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties
                        => GetInstanceProperties() ?? FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                    void IInterceptorSubject.AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
                    {
                        lock (((IInterceptorSubject)this).SyncRoot)
                        {
                            _properties = ((IInterceptorSubject)this).Properties
                                .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
                                .ToFrozenDictionary();
                        }
                    }

                    protected IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties() => _properties;

                    protected TProperty GetPropertyValue<TProperty>(string propertyName, Func<IInterceptorSubject, TProperty> readValue)
                        => _context is not null ? _context.GetPropertyValue(propertyName, readValue)! : readValue(this)!;

                    protected bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, Func<IInterceptorSubject, TProperty> readValue, Action<IInterceptorSubject, TProperty> setValue)
                    {
                        if (_context is null)
                        {
                            setValue(this, newValue);
                            return true;
                        }

                        return _context.SetPropertyValue(propertyName, newValue, readValue, setValue);
                    }

                    protected object? InvokeMethod(string methodName, Func<IInterceptorSubject, object?[], object?> invokeMethod, params object?[] parameters)
                        => _context is not null ? _context.InvokeMethod(methodName, parameters, invokeMethod) : invokeMethod(this, parameters);
                }

                [InterceptorSubject]
                public partial class GenericDerived : GenericBase<SubjectPropertyMetadata>
                {
                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);

        // Assert: derived mode, so no interception members of its own, and no contract diagnostic.
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "NI0007" || d.Id == "NI0062");
        Assert.DoesNotContain("private IInterceptorExecutor? _context;", result.SingleSource());
    }

    /// <summary>
    /// The single fenced code block in docs/generator.md that holds the base class
    /// satisfying the whole contract, together with the generated subclass it hosts.
    /// </summary>
    private static string ReadDocumentedConformingBaseFixture()
    {
        var blocks = new List<string>();
        var currentBlock = new List<string>();
        var insideBlock = false;

        foreach (var line in File.ReadAllLines(FindRepositoryFile(Path.Combine("docs", "generator.md"))))
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (insideBlock)
                {
                    blocks.Add(string.Join(Environment.NewLine, currentBlock));
                    currentBlock.Clear();
                }

                insideBlock = !insideBlock;
                continue;
            }

            if (insideBlock)
            {
                currentBlock.Add(line);
            }
        }

        return Assert.Single(blocks, block => block.Contains("class TrackedEntityBase", StringComparison.Ordinal));
    }

    private static string FindRepositoryFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"Could not find '{relativePath}' in any directory above '{AppContext.BaseDirectory}'. This test " +
            "reads its fixture from docs/generator.md so the documented base cannot drift from the " +
            "tested one, which requires running inside the repository tree.");
    }

    /// <summary>
    /// Counts the executor fields over the whole hierarchy. A hand-written base that really hosts
    /// its generated subclass carries the only one; a subclass that emits its own interception members instead
    /// adds a second that nothing above it ever populates.
    /// </summary>
    private static int CountExecutorFields(Type type)
    {
        var count = 0;
        for (var current = type; current is not null; current = current.BaseType)
        {
            count += current
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Count(field => field.Name == "_context");
        }

        return count;
    }
}
