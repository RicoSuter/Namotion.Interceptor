using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

public class SubjectNotificationInheritanceTests
{
    [Fact]
    public void WhenBaseImplementsRaisePropertyChangedWithoutBeingASubject_ThenNoNotifyMembersAreRedeclared()
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
    public void WhenAttributedAncestorRaisesThroughAnExplicitImplementation_ThenTheSetterCallsItThroughTheInterface()
    {
        // Arrange: Middle carries the attribute but emits no RaisePropertyChanged of its own,
        // because ManualInpcBase already provides the INPC members, and that base implements the
        // raise explicitly. Leaf's attributed ancestor therefore exposes no member of that name and
        // a simple-name call from Leaf is CS0103.
        const string source = """
            using System.ComponentModel;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public abstract class ManualInpcBase : INotifyPropertyChanged, IRaisePropertyChanged
                {
                    public event PropertyChangedEventHandler? PropertyChanged;

                    void IRaisePropertyChanged.RaisePropertyChanged(string propertyName)
                        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
                }

                [InterceptorSubject]
                public partial class Middle : ManualInpcBase
                {
                    public partial string MiddleName { get; set; }
                }

                [InterceptorSubject]
                public partial class Leaf : Middle
                {
                    public partial string LeafName { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);
        var middle = Assert.Single(result.Sources, s => s.HintName.Contains("Repro.Middle.g.cs")).SourceText.ToString();
        var leaf = Assert.Single(result.Sources, s => s.HintName.Contains("Repro.Leaf.g.cs")).SourceText.ToString();

        // Assert
        Assert.DoesNotContain("void RaisePropertyChanged(string propertyName)", middle);
        Assert.Contains("((IRaisePropertyChanged)this).RaisePropertyChanged(nameof(LeafName))", leaf);
    }

    [Fact]
    public void WhenTheRaiseSitsAboveTheAttributedAncestor_ThenTheSetterStillCallsItBySimpleName()
    {
        // Arrange: the same shape as above except that ManualInpcBase implements the raise as an
        // ordinary public member. Middle still emits none of its own, so only a walk of the whole
        // chain finds the member that answers Leaf's call; a lookup stopping at the attributed
        // ancestor would drop Leaf to the interface form. This is the shipped ManualInpcPersonBase
        // shape from Namotion.Interceptor.Tracking.Tests.
        const string source = """
            using System.ComponentModel;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public abstract class ManualInpcBase : INotifyPropertyChanged, IRaisePropertyChanged
                {
                    public event PropertyChangedEventHandler? PropertyChanged;

                    public void RaisePropertyChanged(string propertyName)
                        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
                }

                [InterceptorSubject]
                public partial class Middle : ManualInpcBase
                {
                    public partial string MiddleName { get; set; }
                }

                [InterceptorSubject]
                public partial class Leaf : Middle
                {
                    public partial string LeafName { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);
        var middle = Assert.Single(result.Sources, s => s.HintName.Contains("Repro.Middle.g.cs")).SourceText.ToString();
        var leaf = Assert.Single(result.Sources, s => s.HintName.Contains("Repro.Leaf.g.cs")).SourceText.ToString();

        // Assert: the boundary between the two forms. Middle has no attributed ancestor at all and
        // keeps the interface form, Leaf has one and reaches the inherited member directly.
        Assert.Contains("((IRaisePropertyChanged)this).RaisePropertyChanged(nameof(MiddleName))", middle);
        Assert.Contains("RaisePropertyChanged(nameof(LeafName));", leaf);
        Assert.DoesNotContain("((IRaisePropertyChanged)this).RaisePropertyChanged(nameof(LeafName))", leaf);
    }

    [Fact]
    public void WhenReferencedAttributedBaseRaisesThroughAnExplicitImplementation_ThenTheSetterCallsItThroughTheInterface()
    {
        // Arrange: the same shape across an assembly boundary. The base satisfies every contract
        // clause, so the subject takes derived mode, but its raise is reachable through the
        // interface only.
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
                public class ExplicitRaiseBase : IInterceptorSubject, INotifyPropertyChanged, IRaisePropertyChanged
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

        const string mainSource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace App
            {
                [InterceptorSubject]
                public partial class AppLeaf : Library.ExplicitRaiseBase
                {
                    public partial string LeafName { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunWithLibraryReference(librarySource, mainSource);
        var generated = result.SingleSource();

        // Assert: derived mode, no diagnostic, and the only call form that binds.
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "NI0007" || d.Id == "NI0062");
        Assert.Contains("((IRaisePropertyChanged)this).RaisePropertyChanged(nameof(LeafName))", generated);
    }

    [Fact]
    public void WhenReferencedAttributedBaseHasNoNotifyMembers_ThenTheSubjectDeclaresItsOwn()
    {
        // Arrange: the attribute alone is not evidence that the base owns the INPC members. This
        // base owns none of it, so a simple-name call is CS0103 and an interface cast throws at
        // runtime; the subject has to declare those members itself.
        const string librarySource = """
            using System.Collections.Generic;
            using System.Collections.Frozen;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Library
            {
                [InterceptorSubject]
                public class NoNotifyBase
                {
                    public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; }
                        = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;
                }
            }
            """;

        const string mainSource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace App
            {
                [InterceptorSubject]
                public partial class AppLeaf : Library.NoNotifyBase
                {
                    public partial string LeafName { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunWithLibraryReference(librarySource, mainSource);
        var generated = result.SingleSource();

        // Assert: the base only provides DefaultProperties, so it takes the NI0062 root-mode
        // fallback and declares the notify members it then calls by simple name.
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0062");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        Assert.Contains("IInterceptorSubject, INotifyPropertyChanged, IRaisePropertyChanged", generated);
        Assert.Contains("public event PropertyChangedEventHandler? PropertyChanged;", generated);
        Assert.Contains("RaisePropertyChanged(nameof(LeafName))", generated);
        Assert.DoesNotContain("((IRaisePropertyChanged)this).RaisePropertyChanged", generated);
    }

    [Fact]
    public void WhenRootModeSitsOnAnMvvmBase_ThenTheRedeclaredNotifyMembersCarryNew()
    {
        // Arrange: an ordinary MVVM base. It is not a subject ancestor, so root mode re-emits the
        // whole notify block, and both members it emits already exist above it. The base also
        // carries an interception member name to cover the helper half of the same lookup.
        const string source = """
            using System.ComponentModel;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public class ViewModelBase : INotifyPropertyChanged
                {
                    public event PropertyChangedEventHandler? PropertyChanged;

                    public object? InvokeMethod { get; set; }

                    protected void RaisePropertyChanged(string propertyName)
                        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
                }

                [InterceptorSubject]
                public partial class OnViewModelBase : ViewModelBase
                {
                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);
        var generated = result.SingleSource();

        // Assert: without the modifiers this is three CS0108 in a file the consumer cannot edit.
        Assert.Contains("new public event PropertyChangedEventHandler? PropertyChanged;", generated);
        Assert.Contains("new protected void RaisePropertyChanged(string propertyName)", generated);
        Assert.Contains("new protected object? InvokeMethod(", generated);
    }

    [Fact]
    public void WhenTheCollidingBaseMemberIsStatic_ThenTheRedeclaredMemberStillCarriesNew()
    {
        // Arrange: C# hiding is not staticness-sensitive, so a static base member of an interception member name is hidden by the emitted instance member exactly like an instance one.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public class StaticHolderBase
                {
                    public static object? GetInstanceProperties { get; set; }

                    protected static void RaisePropertyChanged(string propertyName) { }
                }

                [InterceptorSubject]
                public partial class OnStaticHolderBase : StaticHolderBase
                {
                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);
        var generated = result.SingleSource();

        // Assert
        Assert.Contains("new protected IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties()", generated);
        Assert.Contains("new protected void RaisePropertyChanged(string propertyName)", generated);
    }

    [Fact]
    public void WhenTheBaseRaiseTakesEventArgs_ThenNoNewModifierIsEmitted()
    {
        // Arrange: RaisePropertyChanged(PropertyChangedEventArgs) shares only the name with the
        // emitted RaisePropertyChanged(string), so C# hiding does not apply to it. A 'new' here
        // would be CS0109, which fails a consumer build exactly like the CS0108 it guards against.
        const string source = """
            using System.ComponentModel;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public class ArgsOnlyBase
                {
                    protected void RaisePropertyChanged(PropertyChangedEventArgs args) { }
                }

                [InterceptorSubject]
                public partial class OnArgsOnlyBase : ArgsOnlyBase
                {
                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);
        var generated = result.SingleSource();

        // Assert
        Assert.DoesNotContain("new protected void RaisePropertyChanged", generated);
        Assert.Contains("protected void RaisePropertyChanged(string propertyName)", generated);
    }
}
