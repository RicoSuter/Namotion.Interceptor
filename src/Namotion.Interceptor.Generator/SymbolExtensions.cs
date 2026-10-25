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
    /// Checks whether the type is or implements the named interface, including inherited interfaces.
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
    /// Enumerates accessible instance members with the given name along the base chain.
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
    /// Enumerates accessible base members with the given name, including statics that instance members can hide.
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
    /// Identifies indexers and static properties, which cannot become subject properties.
    /// </summary>
    /// <remarks>
    /// Ignore these shapes without NI0040, including explicit implementations and interface defaults.
    /// </remarks>
    public static bool IsNeverASubjectProperty(IPropertySymbol property)
    {
        return property.IsIndexer || property.IsStatic;
    }

    /// <summary>
    /// Checks accessor reachability from <paramref name="typeSymbol"/> through a receiver cast to <paramref name="throughType"/>.
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

        // Individual accessors can be less accessible than the property.
        var isGetterAccessible = member.GetMethod is { } getMethod &&
            compilation.IsSymbolAccessibleWithin(getMethod, typeSymbol, throughType);
        var isSetterAccessible = member.SetMethod is { } setMethod &&
            compilation.IsSymbolAccessibleWithin(setMethod, typeSymbol, throughType);

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
