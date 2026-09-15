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
        // Arrange: one of the two directions goal 4 asks for. This was CS0122 on every helper the
        // subclass touches while the generator emitted them private, so the shape is pinned rather
        // than left to the emitter's modifier choice.
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
                        set => SetPropertyValue(nameof(Own), value, _own, static (o, v) => ((HandDerived)o)._own = v);
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
        // Arrange: the test above proves the helpers are reachable, this one proves they work. A
        // hand-written subclass has no generated DefaultProperties, so its metadata only exists once
        // AddProperties has run, and the base's ": base(context)" constructor publishes the executor
        // before this constructor body starts. Registering after the first write would therefore
        // throw from PropertyReference.Metadata rather than silently skip interception.
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
                        set => SetPropertyValue(nameof(Own), value, _own, static (o, v) => ((HandDerived)o)._own = v);
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
        // Arrange: the other of the two directions goal 4 asks for, and the only test that runs it.
        // The fixture is read out of docs/generator.md instead of being copied here, so the
        // contract the documentation asks a base class to satisfy and the contract this test proves
        // cannot drift apart.
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

        // Assert: the generated setter routes through the hand-written base's SetPropertyValue and
        // lands on the executor the base's Context published. The field count is what shows it is
        // the base's and not a second copy emitted into the subclass, and the warning check is what
        // shows that second copy is not merely unused but absent: a private helper hiding the
        // inherited protected one is CS0108 in a file the consumer cannot edit.
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
        // Arrange: an attributed base built by an older generator, so its helpers are private and it
        // has no GetInstanceProperties at all. Either cause alone fails the contract, so the
        // fallback is what the assertions pin, not one specific missing member.
        // Branch 1's "declared in source" qualifier is what stops this from selecting derived mode
        // and emitting CS0122 calls into generated code. The generator is NOT run over the library.
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

                    private bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> setValue)
                    {
                        if (_context is null)
                        {
                            setValue(this, newValue);
                            return true;
                        }

                        return _context.SetPropertyValue(propertyName, newValue, currentValue, setValue);
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
        // Arrange: the subject derives from a constructed GenericBase<SubjectPropertyMetadata>, so
        // the contract lookup has to see the substituted members, not the open definition's.
        // DefaultProperties is declared in terms of T on purpose: its type only equals the
        // IReadOnlyDictionary<string, SubjectPropertyMetadata> the check compares against once the
        // type argument is substituted, so a lookup running against the open definition fails.
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
