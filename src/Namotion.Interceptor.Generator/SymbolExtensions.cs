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
