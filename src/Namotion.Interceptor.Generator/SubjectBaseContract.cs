using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Namotion.Interceptor.Generator;

/// <summary>
/// Everything the generator needs to know about the class a subject inherits from: which ancestor
/// owns the shared plumbing, and whether that ancestor exposes enough of it to be inherited from.
/// </summary>
internal static class SubjectBaseContract
{
    /// <summary>
    /// The first ancestor that is a subject, skipping ordinary classes in between. Plain classes
    /// between two subjects are common enough to matter and reading the immediate base instead
    /// makes the generator emit a second copy of everything it already inherited.
    /// </summary>
    public static INamedTypeSymbol? FindNearestSubjectAncestor(INamedTypeSymbol typeSymbol)
    {
        for (var ancestor = typeSymbol.BaseType;
             ancestor is { SpecialType: not SpecialType.System_Object };
             ancestor = ancestor.BaseType)
        {
            if (HasInterceptorSubjectAttribute(ancestor) || DeclaresInterceptorSubject(ancestor))
            {
                return ancestor;
            }
        }

        return null;
    }

    public static bool HasInterceptorSubjectAttribute(INamedTypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }

        return type
            .GetAttributes()
            .Any(a => SymbolExtensions.IsTypeOrInheritsFrom(a.AttributeClass, KnownTypes.InterceptorSubjectAttribute));
    }

    /// <summary>
    /// Whether the type itself declares IInterceptorSubject, directly or through an interface it
    /// declares. Deliberately not AllInterfaces and deliberately no BaseType recursion: those
    /// report interfaces inherited from a base class, which would stop the ancestor walk at a
    /// plain intermediate whenever the real subject ancestor comes from a metadata reference,
    /// that is, in every cross-assembly hierarchy.
    /// </summary>
    private static bool DeclaresInterceptorSubject(INamedTypeSymbol type)
    {
        return type.Interfaces.Any(declared =>
            IsInterceptorSubject(declared) || declared.AllInterfaces.Any(IsInterceptorSubject));
    }

    private static bool IsInterceptorSubject(INamedTypeSymbol interfaceType)
    {
        return interfaceType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == KnownTypes.IInterceptorSubject;
    }

    /// <summary>
    /// The shape of one helper method root mode emits, in the single place both the contract check
    /// and the hiding check read it from. Parameter types are approximated by counts, which is
    /// enough to separate the emitted signature from an unrelated overload of the same name.
    /// </summary>
    private sealed class PlumbingMethodShape
    {
        public PlumbingMethodShape(
            string name,
            int typeParameterCount,
            int parameterCount,
            bool requiresParameterArray,
            string declaration)
        {
            Name = name;
            TypeParameterCount = typeParameterCount;
            ParameterCount = parameterCount;
            RequiresParameterArray = requiresParameterArray;
            Declaration = declaration;
        }

        public string Name { get; }

        public int TypeParameterCount { get; }

        public int ParameterCount { get; }

        /// <summary>
        /// Consulted by the contract check only. The emitted call site uses expanded form,
        /// InvokeMethod("M", lambda, p1), so a base declaring the same parameter types without
        /// params would pass a signature match and then fail at the call. Hiding is the opposite:
        /// params is not part of the signature C# hides by, so such a base does hide the emitted
        /// method and the 'new' modifier is still required.
        /// </summary>
        public bool RequiresParameterArray { get; }

        /// <summary>
        /// How the member is named in the NI0011 message.
        /// </summary>
        public string Declaration { get; }
    }

    private static readonly PlumbingMethodShape[] PlumbingMethods =
    [
        new PlumbingMethodShape(
            "GetPropertyValue", typeParameterCount: 1, parameterCount: 2, requiresParameterArray: false,
            "protected TProperty GetPropertyValue<TProperty>(string, Func<IInterceptorSubject, TProperty>)"),
        new PlumbingMethodShape(
            "SetPropertyValue", typeParameterCount: 1, parameterCount: 4, requiresParameterArray: false,
            "protected bool SetPropertyValue<TProperty>(string, TProperty, TProperty, Action<IInterceptorSubject, TProperty>)"),
        new PlumbingMethodShape(
            "InvokeMethod", typeParameterCount: 0, parameterCount: 3, requiresParameterArray: true,
            "protected object? InvokeMethod(string, Func<IInterceptorSubject, object?[], object?>, params object?[])"),
        new PlumbingMethodShape(
            "GetInstanceProperties", typeParameterCount: 0, parameterCount: 0, requiresParameterArray: false,
            "protected IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties()")
    ];

    /// <summary>
    /// The members a class must expose to host a generated subclass. Generated root mode satisfies
    /// this by construction. Lookup walks the ancestor chain, so a member inherited by the ancestor
    /// from further up counts, and runs against the constructed type, so a generic base is checked
    /// with its type arguments substituted.
    /// </summary>
    /// <remarks>
    /// Every clause but one is required for the generated code to compile. The
    /// <see cref="KnownTypes.IRaisePropertyChanged"/> clause is deliberately stricter than that:
    /// when it is the only clause a shape fails, derived mode would still compile, because the
    /// subject declares its own PropertyChanged and RaisePropertyChanged whenever the base has
    /// neither. Failing the contract there costs that shape the plumbing and allocation sharing of
    /// derived mode, not the build.
    /// </remarks>
    public static bool SatisfiesContract(
        INamedTypeSymbol ancestor,
        INamedTypeSymbol subject,
        Compilation compilation,
        out IReadOnlyList<string> missingMembers)
    {
        var missing = new List<string>();

        if (!ImplementsInterfaceThroughChain(ancestor, KnownTypes.IInterceptorSubject))
        {
            missing.Add(KnownTypes.IInterceptorSubject);
        }

        if (!ImplementsInterfaceThroughChain(ancestor, KnownTypes.IRaisePropertyChanged) &&
            !ImplementsInterfaceThroughChain(subject, KnownTypes.IRaisePropertyChanged))
        {
            missing.Add(KnownTypes.IRaisePropertyChanged);
        }

        foreach (var plumbingMethod in PlumbingMethods)
        {
            if (!HasAccessibleMethod(ancestor, subject, compilation, plumbingMethod))
            {
                missing.Add(plumbingMethod.Declaration);
            }
        }

        if (!HasUsableDefaultProperties(ancestor, subject, compilation))
        {
            missing.Add("public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties");
        }

        missingMembers = missing;
        return missing.Count == 0;
    }

    /// <summary>
    /// A static DefaultProperties that is both accessible and of a type the emitted .Concat(...)
    /// accepts. Checking only that some static of that name resolves lets a base declaring
    /// "public static int DefaultProperties" through, and the generated code then fails with
    /// CS1929, which is exactly the raw compiler error in generated code the diagnostics exist to
    /// replace. The type test compares against the constructed
    /// IReadOnlyDictionary&lt;string, SubjectPropertyMetadata&gt; rather than matching on the display
    /// string, which would accept an IReadOnlyList&lt;SubjectPropertyMetadata&gt; and produce that
    /// same CS1929. A field counts as well as a property: the emitted call site reads it the same
    /// way, so rejecting one would turn a shape that compiles today into an NI0011 error.
    /// </summary>
    public static bool HasUsableDefaultProperties(INamedTypeSymbol ancestor, INamedTypeSymbol subject, Compilation compilation)
    {
        var expectedType = GetPropertyMetadataDictionaryType(compilation);
        if (expectedType is null)
        {
            return false;
        }

        foreach (var candidate in EnumerateChain(ancestor))
        {
            foreach (var member in candidate.GetMembers("DefaultProperties"))
            {
                var memberType = member switch
                {
                    IPropertySymbol property => property.Type,
                    IFieldSymbol field => field.Type,
                    _ => null
                };

                if (memberType is null ||
                    !member.IsStatic ||
                    !compilation.IsSymbolAccessibleWithin(member, subject))
                {
                    continue;
                }

                // The declared type itself, or any type that implements it: a Dictionary or a
                // FrozenDictionary converts to it implicitly and the emitted .Concat(...) consumes
                // it just as happily.
                if (SymbolEqualityComparer.Default.Equals(memberType, expectedType) ||
                    memberType.AllInterfaces.Contains(expectedType, SymbolEqualityComparer.Default))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// IReadOnlyDictionary&lt;string, SubjectPropertyMetadata&gt; as the compilation sees it, or null
    /// when either half is unreferenced, in which case nothing the generator emits would compile
    /// anyway.
    /// </summary>
    private static INamedTypeSymbol? GetPropertyMetadataDictionaryType(Compilation compilation)
    {
        var dictionaryType = compilation.GetTypeByMetadataName("System.Collections.Generic.IReadOnlyDictionary`2");
        var propertyMetadataType = compilation.GetTypeByMetadataName(KnownTypes.SubjectPropertyMetadata);

        if (dictionaryType is null || propertyMetadataType is null)
        {
            return null;
        }

        return dictionaryType.Construct(compilation.GetSpecialType(SpecialType.System_String), propertyMetadataType);
    }

    /// <summary>
    /// The root-mode member names that need a 'new' modifier because the ancestor chain already
    /// exposes an accessible member the emitted one hides. This is C#'s hiding rule, not the
    /// contract's match: CS0108 fires for a same-name member of a DIFFERENT kind too, while a
    /// blanket 'new' produces CS0109 when nothing is hidden. Both are build errors under
    /// TreatWarningsAsErrors, so the modifier has to be decided per member.
    /// </summary>
    public static IReadOnlyList<string> FindHiddenPlumbingMembers(
        INamedTypeSymbol? ancestor,
        INamedTypeSymbol subject,
        Compilation compilation)
    {
        if (ancestor is null)
        {
            return [];
        }

        var hidden = new List<string>();

        foreach (var plumbingMethod in PlumbingMethods)
        {
            var isHidden = EnumerateChain(ancestor)
                .SelectMany(type => type.GetMembers(plumbingMethod.Name))
                .Any(member =>
                    !member.IsStatic &&
                    compilation.IsSymbolAccessibleWithin(member, subject) &&
                    IsHiddenByEmittedMember(member, plumbingMethod));

            if (isHidden)
            {
                hidden.Add(plumbingMethod.Name);
            }
        }

        return hidden;
    }

    /// <summary>
    /// A method is hidden only when its signature matches the emitted one, so an unrelated overload
    /// of a plumbing name hides nothing and must not attract a 'new'. Everything else hides by name
    /// alone: a base property or field named GetPropertyValue is hidden by the emitted method.
    /// </summary>
    private static bool IsHiddenByEmittedMember(ISymbol member, PlumbingMethodShape plumbingMethod)
    {
        if (member is not IMethodSymbol method)
        {
            return true;
        }

        return method.TypeParameters.Length == plumbingMethod.TypeParameterCount &&
               method.Parameters.Length == plumbingMethod.ParameterCount;
    }

    private static bool HasAccessibleMethod(
        INamedTypeSymbol ancestor,
        INamedTypeSymbol subject,
        Compilation compilation,
        PlumbingMethodShape plumbingMethod)
    {
        foreach (var candidate in EnumerateChain(ancestor))
        {
            foreach (var method in candidate.GetMembers(plumbingMethod.Name).OfType<IMethodSymbol>())
            {
                if (method.IsStatic ||
                    method.TypeParameters.Length != plumbingMethod.TypeParameterCount ||
                    method.Parameters.Length != plumbingMethod.ParameterCount ||
                    !compilation.IsSymbolAccessibleWithin(method, subject))
                {
                    continue;
                }

                if (plumbingMethod.RequiresParameterArray &&
                    !method.Parameters[method.Parameters.Length - 1].IsParams)
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateChain(INamedTypeSymbol type)
    {
        for (var current = type; current is { SpecialType: not SpecialType.System_Object }; current = current.BaseType)
        {
            yield return current;
        }
    }

    private static bool ImplementsInterfaceThroughChain(INamedTypeSymbol type, string interfaceTypeName)
    {
        return type.AllInterfaces.Any(i => i.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == interfaceTypeName);
    }
}
