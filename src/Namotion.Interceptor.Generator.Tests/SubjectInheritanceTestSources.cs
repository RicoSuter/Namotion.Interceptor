namespace Namotion.Interceptor.Generator.Tests;

internal static class SubjectInheritanceTestSources
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

    internal static string LeafDeclaring(string memberDeclaration)
        => LeafMemberTemplate.Replace("{0}", memberDeclaration);

    /// <summary>
    /// The ordinary hand-written subject: it satisfies the contract with plain public members and
    /// derives from object, so it has nothing above it whose interface slot it could take. This is
    /// the shape NI0064 must stay silent on, and it has to keep intercepting.
    /// </summary>
    internal const string PublicMemberBase = """
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

    internal const string GeneratedDerived = """

        namespace Repro
        {
            [Namotion.Interceptor.Attributes.InterceptorSubject]
            public partial class GenDerived : HandBase
            {
                public partial string Name { get; set; }
            }
        }
        """;
}
