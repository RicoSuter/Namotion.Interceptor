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
}
