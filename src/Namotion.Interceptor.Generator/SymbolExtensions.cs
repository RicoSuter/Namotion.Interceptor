using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Namotion.Interceptor.Generator;

internal static class SymbolExtensions
{
    public static bool HasAttribute(
        SyntaxList<AttributeListSyntax> attributeLists,
        string baseTypeName,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        return attributeLists
            .SelectMany(al => al.Attributes)
            .Any(attribute =>
            {
                var attributeType = semanticModel.GetTypeInfo(attribute, cancellationToken).Type as INamedTypeSymbol;
                return attributeType is not null && IsTypeOrInheritsFrom(attributeType, baseTypeName);
            });
    }

    /// <summary>
    /// Whether the type implements the named interface, including through a base class and through
    /// interface inheritance. AllInterfaces already covers both, so no recursion is needed.
    /// </summary>
    public static bool ImplementsInterface(ITypeSymbol? type, string interfaceTypeName)
    {
        if (type is null)
        {
            return false;
        }

        if (type.TypeKind == TypeKind.Interface &&
            type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == interfaceTypeName)
        {
            return true;
        }

        return type.AllInterfaces.Any(i =>
            i.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == interfaceTypeName);
    }

    public static bool IsTypeOrInheritsFrom(ITypeSymbol? type, string fullTypeName)
    {
        while (type is not null)
        {
            if (type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == fullTypeName)
            {
                return true;
            }
            type = type.BaseType;
        }
        return false;
    }

    /// <summary>
    /// The type and every class above it, stopping before object.
    /// </summary>
    public static IEnumerable<INamedTypeSymbol> EnumerateChain(INamedTypeSymbol? type)
    {
        for (var current = type; current is { SpecialType: not SpecialType.System_Object }; current = current.BaseType)
        {
            yield return current;
        }
    }

    /// <summary>
    /// The members of a given name that member lookup from <paramref name="accessingType"/> would find
    /// on the chain starting at <paramref name="baseType"/>. Statics are dropped because none of the
    /// call sites the generator emits can reach one, and inaccessible members because they neither
    /// hide nor bind. Contrast <see cref="HidableMembers"/>, which must see statics.
    /// </summary>
    public static IEnumerable<ISymbol> AccessibleMembers(
        INamedTypeSymbol baseType,
        INamedTypeSymbol accessingType,
        Compilation compilation,
        string name)
        => EnumerateChain(baseType)
            .SelectMany(type => type.GetMembers(name))
            .Where(member => !member.IsStatic && compilation.IsSymbolAccessibleWithin(member, accessingType));

    /// <summary>
    /// The members of a given name on the base chain that a member emitted into
    /// <paramref name="accessingType"/> can hide. Same as <see cref="AccessibleMembers"/> except that
    /// statics are kept: C# hiding is not staticness-sensitive, so a static base member of one of those names is hidden by the emitted instance member and produces the same CS0108 an instance one
    /// would. Accessibility still applies, because an inaccessible member is neither hidden nor found
    /// by member lookup.
    /// </summary>
    public static IEnumerable<ISymbol> HidableMembers(
        INamedTypeSymbol baseType,
        INamedTypeSymbol accessingType,
        Compilation compilation,
        string name)
        => EnumerateChain(baseType)
            .SelectMany(type => type.GetMembers(name))
            .Where(member => compilation.IsSymbolAccessibleWithin(member, accessingType));

    /// <summary>
    /// The shape guard a property must pass to become a subject property, shared between a
    /// property declared in the class body and one adopted from an interface default
    /// implementation: an indexer has no usable name and is parameterised, and a static member
    /// cannot be read from an instance (the emitted accessor lambda always takes an instance).
    /// Kept as one rule both paths consult, rather than one each, because the two paths have
    /// already drifted apart once before, on accessibility.
    /// </summary>
    /// <remarks>
    /// Neither shape is reported: NI0040 speaks to a member that could plausibly have become a
    /// subject property and did not, and neither an indexer nor a static member was ever a
    /// candidate. A class-declared indexer has always been ignored in silence (it parses as
    /// <c>IndexerDeclarationSyntax</c>, which the property filter excludes before this guard runs),
    /// so reporting the interface-default form was also an inconsistency between the two paths.
    /// This rule outranks the explicit-implementation opt-in, which <c>ClassPropertyMetadataExtractor</c>
    /// checks after it. A <c>static abstract</c> interface member forces a static implementation on the
    /// subject, and no edit the author can make would turn it into a property.
    /// </remarks>
    public static bool IsNeverASubjectProperty(IPropertySymbol property)
    {
        return property.IsIndexer || property.IsStatic;
    }

    /// <summary>
    /// Resolves per-accessor reachability of an interface member from generated code living inside
    /// <paramref name="typeSymbol"/>, accessed through a receiver cast to <paramref name="throughType"/>.
    /// Shared by the interface default-implementation path and the class explicit-implementation
    /// path, since both reach the member through the same "cast to the interface" pattern and are
    /// governed by the same accessibility rule.
    /// </summary>
    public static (bool IsGetterAccessible, bool IsSetterAccessible) GetAccessorAccessibility(
        Compilation compilation,
        IPropertySymbol member,
        INamedTypeSymbol typeSymbol,
        ITypeSymbol throughType)
    {
        if (!compilation.IsSymbolAccessibleWithin(member, typeSymbol, throughType))
        {
            return (false, false);
        }

        // A getter or setter can be individually less accessible than the property itself
        // (e.g. `string Probe { get; private set; }`); generated code accesses whichever accessor
        // it emits directly, so each one needs its own reachability check.
        var isGetterAccessible = member.GetMethod is { } getMethod &&
            compilation.IsSymbolAccessibleWithin(getMethod, typeSymbol, throughType);
        var isSetterAccessible = member.SetMethod is { } setMethod &&
            compilation.IsSymbolAccessibleWithin(setMethod, typeSymbol, throughType);

        // Both false here (with the property-level check above having passed) is believed
        // unreachable: C# forbids an accessor modifier on both accessors at once, and requires any
        // accessor modifier to be strictly more restrictive than the property, so the accessor
        // without a modifier is accessible by construction whenever the property-level check
        // passed. Kept defensive rather than assumed, in case that invariant stops holding.
        return (isGetterAccessible, isSetterAccessible);
    }

    public static string GetAccessModifierFromAccessibility(Accessibility accessibility)
    {
        return accessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            Accessibility.Protected => "protected",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.ProtectedAndInternal => "private protected",
            Accessibility.Private => "private",
            _ => "public"  // Interface members default to public
        };
    }
}
