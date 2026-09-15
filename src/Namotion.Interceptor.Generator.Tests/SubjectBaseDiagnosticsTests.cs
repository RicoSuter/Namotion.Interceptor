using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

public partial class SubjectBaseDiagnosticsTests
{
    /// <summary>
    /// A generated root plus a generated leaf, with <c>{0}</c> replaced by the leaf member under
    /// test. Every NI0063 and NI0064 case is this shape with one member swapped.
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

    /// <summary>
    /// The ordinary hand-written subject: it satisfies the contract with plain public members and
    /// derives from object, so it has nothing above it whose interface slot it could take. This is
    /// the shape NI0064 must stay silent on, and it has to keep intercepting.
    /// </summary>
    private const string PublicMemberBase = """
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

                public void RaisePropertyChanged(string propertyName)
                    => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

                public IInterceptorSubjectContext Context => InterceptorExecutor.GetOrCreate(ref _context, this);
                public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();
                public object SyncRoot { get; } = new object();
                public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => GetInstanceProperties() ?? DefaultProperties;

                public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
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

    /// <summary>
    /// <see cref="PublicMemberBase"/> with a virtual Context, which is what an intermediate class
    /// needs in order to override it rather than hide it.
    /// </summary>
    private static readonly string VirtualContextBase = PublicMemberBase.Replace(
        "public IInterceptorSubjectContext Context",
        "public virtual IInterceptorSubjectContext Context");

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

    private const string ContextWrapperDerived = """

        namespace Repro
        {
            [Namotion.Interceptor.Attributes.InterceptorSubject]
            public partial class GenDerived : HandBase
            {
                public partial string Name { get; set; }

                public string ContextWithoutInterceptor(string tag) => tag;
            }
        }
        """;

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
