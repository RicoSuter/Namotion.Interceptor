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
}
