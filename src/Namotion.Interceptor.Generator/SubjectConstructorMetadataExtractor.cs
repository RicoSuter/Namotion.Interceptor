using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Namotion.Interceptor.Generator.Models;

namespace Namotion.Interceptor.Generator;

internal static class SubjectConstructorMetadataExtractor
{
    /// <summary>
    /// Whether a declared constructor already occupies the context-only call, so emitting
    /// "Subject(IInterceptorSubjectContext)" would either collide with it (CS0111) or, when its
    /// later parameters are all optional or params, win that call by better overload resolution and
    /// leave the hand-written body silently unreachable.
    /// </summary>
    /// <remarks>
    /// A by-reference or params context parameter is a legal overload alongside the by-value form
    /// and does not take the call, and neither does a derived interface or the concrete context
    /// class, so those keep the generated overload. Accessibility does not enter into it: which
    /// call sites can see the declared constructor decides nothing, because the generated overload
    /// wins the call wherever both are visible, so a non-public declaration is displaced from
    /// inside the declaring class exactly as a public one is from outside it.
    /// </remarks>
    public static bool HasDeclaredContextConstructor(INamedTypeSymbol typeSymbol, Compilation compilation)
    {
        var contextType = compilation.GetTypeByMetadataName(KnownTypes.IInterceptorSubjectContext);
        if (contextType is null)
        {
            return false;
        }

        foreach (var constructor in typeSymbol.InstanceConstructors)
        {
            if (TakesContextAlone(constructor, contextType))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Collects the declared instance constructors across all partial declarations, omitting the
    /// ones a mirror could not reproduce faithfully. Parameter types are resolved through each
    /// declaration's own semantic model rather than taken from syntax text, because the generated
    /// partial half does not repeat the declaring file's using directives, so a name like
    /// "List&lt;Foo&gt;" may not resolve there.
    /// </summary>
    public static IReadOnlyList<SubjectConstructor> CollectConstructors(
        TypeDeclarationSyntax[] allTypeDeclarations,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var constructors = new List<SubjectConstructor>();

        foreach (var typeDeclaration in allTypeDeclarations)
        {
            var declarationModel = semanticModel.Compilation.GetSemanticModel(typeDeclaration.SyntaxTree);

            if (typeDeclaration.ParameterList is { } primaryParameters &&
                CollectConstructorParameters(primaryParameters, declarationModel) is { } primaryConstructorParameters)
            {
                constructors.Add(CreatePrimaryConstructor(typeDeclaration, primaryConstructorParameters, declarationModel, cancellationToken));
            }

            foreach (var constructor in typeDeclaration.Members.OfType<ConstructorDeclarationSyntax>())
            {
                // A static constructor has no accessibility and cannot be chained to.
                if (constructor.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
                {
                    continue;
                }

                var parameters = CollectConstructorParameters(constructor.ParameterList, declarationModel);
                if (parameters is null)
                {
                    continue;
                }

                // An obsolete constructor is never mirrored: the mirror's ": this(...)" chain
                // would reference it from the generated file, raising CS0618 (or CS0619 with
                // error: true) in code the consumer does not own. It is still collected, because
                // the emit side matches each mirror it is about to write against the declared
                // signatures; an obsolete constructor already carrying a mirror's shape would
                // otherwise collide with it as CS0111 in a file the consumer cannot edit.
                // Resolved through the semantic model so an alias, the "Attribute" suffix and
                // qualified spellings are all caught, not just the literal identifier "Obsolete".
                var isObsolete = SymbolExtensions.HasAttribute(
                    constructor.AttributeLists, KnownTypes.ObsoleteAttribute, declarationModel, cancellationToken);

                // Carried because a generated constructor chaining to this one has to repeat the
                // attribute; without it the chain is CS9039.
                var setsRequiredMembers = SymbolExtensions.HasAttribute(
                    constructor.AttributeLists, KnownTypes.SetsRequiredMembersAttribute, declarationModel, cancellationToken);

                var accessibility = declarationModel.GetDeclaredSymbol(constructor, cancellationToken)?.DeclaredAccessibility
                    ?? Accessibility.Private;

                constructors.Add(new SubjectConstructor(
                    SymbolExtensions.GetAccessModifierFromAccessibility(accessibility),
                    parameters,
                    isObsolete,
                    setsRequiredMembers));
            }
        }

        return constructors;
    }

    private static bool TakesContextAlone(IMethodSymbol constructor, INamedTypeSymbol contextType)
    {
        if (constructor.Parameters.Length == 0)
        {
            return false;
        }

        var first = constructor.Parameters[0];
        if (first.RefKind != RefKind.None || first.IsParams ||
            !SymbolEqualityComparer.Default.Equals(first.Type, contextType))
        {
            return false;
        }

        for (var index = 1; index < constructor.Parameters.Length; index++)
        {
            if (constructor.Parameters[index] is { IsOptional: false, IsParams: false })
            {
                return false;
            }
        }

        return true;
    }

    private static SubjectConstructor CreatePrimaryConstructor(
        TypeDeclarationSyntax typeDeclaration,
        IReadOnlyList<SubjectConstructorParameter> parameters,
        SemanticModel declarationModel,
        CancellationToken cancellationToken)
    {
        var primaryConstructor = declarationModel.GetDeclaredSymbol(typeDeclaration, cancellationToken)!
            .InstanceConstructors.First(constructor => constructor.DeclaringSyntaxReferences.Any(reference =>
                reference.SyntaxTree == typeDeclaration.SyntaxTree && reference.Span == typeDeclaration.Span));
        var attributes = primaryConstructor.GetAttributes();

        return new SubjectConstructor(
            SymbolExtensions.GetAccessModifierFromAccessibility(primaryConstructor.DeclaredAccessibility),
            parameters,
            attributes.Any(attribute => SymbolExtensions.IsTypeOrInheritsFrom(attribute.AttributeClass, KnownTypes.ObsoleteAttribute)),
            attributes.Any(attribute => SymbolExtensions.IsTypeOrInheritsFrom(attribute.AttributeClass, KnownTypes.SetsRequiredMembersAttribute)));
    }

    private static IReadOnlyList<SubjectConstructorParameter>? CollectConstructorParameters(
        ParameterListSyntax parameterList,
        SemanticModel declarationModel)
    {
        var parameters = new List<SubjectConstructorParameter>(parameterList.Parameters.Count);

        foreach (var parameter in parameterList.Parameters)
        {
            // ref, out, in, params or scoped: the metadata carries no parameter modifier, so a
            // mirror would either not compile or silently change the calling convention. Such a
            // constructor is never mirrored.
            if (parameter.Modifiers.Count > 0)
            {
                return null;
            }

            // A missing type is only reachable with error code, since a constructor parameter
            // cannot omit its type; dropping the whole constructor beats carrying a wrong signature.
            // A pointer or function pointer type compiles only in an unsafe context, which the
            // generated mirror does not have; "unsafe" sits on the constructor, so the modifier
            // check above never sees it.
            var parameterType = parameter.Type is not null
                ? declarationModel.GetTypeInfo(parameter.Type).Type
                : null;
            if (parameterType is null || ContainsPointerType(parameterType))
            {
                return null;
            }

            // Text rather than ValueText: the latter drops the escape from a verbatim identifier,
            // so a parameter named "@event" would be re-emitted as the keyword "event".
            parameters.Add(new SubjectConstructorParameter(
                parameterType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                parameter.Identifier.Text));
        }

        return parameters;
    }

    private static bool ContainsPointerType(ITypeSymbol type)
    {
        // Arrays are unwrapped because int*[] carries the same unsafe requirement.
        while (type is IArrayTypeSymbol array)
        {
            type = array.ElementType;
        }

        return type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer;
    }
}
