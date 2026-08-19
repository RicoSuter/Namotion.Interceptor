using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Namotion.Interceptor.Generator;

internal static class SubjectPropertySymbolClassifier
{
    public static bool CanContainSubjects(ITypeSymbol? type)
    {
        if (type is null || type.TypeKind is TypeKind.Error or TypeKind.TypeParameter)
        {
            return true;
        }

        return IsSubjectReferenceType(type) ||
               IsSubjectCollectionType(type) ||
               IsSubjectDictionaryType(type);
    }

    private static bool IsSubjectReferenceType(ITypeSymbol type)
    {
        if (IsInterceptorSubject(type))
        {
            return true;
        }

        return (type.SpecialType == SpecialType.System_Object || type.TypeKind == TypeKind.Interface) &&
               !ImplementsInterface(type, "System.Collections", "IEnumerable");
    }

    private static bool IsSubjectCollectionType(ITypeSymbol type)
    {
        if (IsInterceptorSubject(type) ||
            IsSubjectDictionaryType(type) ||
            !ImplementsInterface(type, "System.Collections", "IEnumerable"))
        {
            return false;
        }

        var genericEnumerables = GetGenericInterfacesIncludingSelf(type, "System.Collections.Generic", "IEnumerable`1");
        if (genericEnumerables.Any())
        {
            return genericEnumerables.Any(enumerable =>
                IsCandidateElementType(enumerable.TypeArguments[0]));
        }

        return ImplementsInterface(type, "System.Collections", "ICollection");
    }

    private static bool IsSubjectDictionaryType(ITypeSymbol type)
    {
        if (IsInterceptorSubject(type))
        {
            return false;
        }

        if (!ImplementsInterface(type, "System.Collections", "IDictionary") &&
            !ImplementsInterface(type, "System.Collections.Generic", "IDictionary`2") &&
            !ImplementsInterface(type, "System.Collections.Generic", "IReadOnlyDictionary`2"))
        {
            return false;
        }

        var genericEnumerables = GetGenericInterfacesIncludingSelf(type, "System.Collections.Generic", "IEnumerable`1");
        if (genericEnumerables.Any())
        {
            return genericEnumerables.Any(enumerable =>
                enumerable.TypeArguments[0] is INamedTypeSymbol { TypeArguments.Length: 2 } keyValuePair &&
                IsNamedType(keyValuePair, "System.Collections.Generic", "KeyValuePair`2") &&
                IsCandidateElementType(keyValuePair.TypeArguments[1]));
        }

        return true;
    }

    private static bool IsCandidateElementType(ITypeSymbol type) =>
        IsInterceptorSubject(type) ||
        ((type.SpecialType == SpecialType.System_Object || type.TypeKind == TypeKind.Interface) &&
         !ImplementsInterface(type, "System.Collections", "IEnumerable"));

    private static bool IsInterceptorSubject(ITypeSymbol type) =>
        SymbolExtensions.ImplementsInterface(type, KnownTypes.IInterceptorSubject) ||
        type is INamedTypeSymbol namedType &&
        (SubjectAncestry.HasInterceptorSubjectAttribute(namedType) ||
         SubjectAncestry.FindNearestSubjectAncestor(namedType) is not null);

    private static bool ImplementsInterface(ITypeSymbol type, string namespaceName, string metadataName) =>
        GetInterfacesIncludingSelf(type).Any(interfaceType => IsNamedType(interfaceType, namespaceName, metadataName));

    private static IEnumerable<INamedTypeSymbol> GetGenericInterfacesIncludingSelf(
        ITypeSymbol type,
        string namespaceName,
        string metadataName) =>
        GetInterfacesIncludingSelf(type).Where(interfaceType => IsNamedType(interfaceType, namespaceName, metadataName));

    private static IEnumerable<INamedTypeSymbol> GetInterfacesIncludingSelf(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol namedType)
        {
            yield return namedType;
        }

        foreach (var interfaceType in type.AllInterfaces)
        {
            yield return interfaceType;
        }
    }

    private static bool IsNamedType(INamedTypeSymbol type, string namespaceName, string metadataName) =>
        type.OriginalDefinition.MetadataName == metadataName &&
        type.OriginalDefinition.ContainingNamespace.ToDisplayString() == namespaceName;
}
