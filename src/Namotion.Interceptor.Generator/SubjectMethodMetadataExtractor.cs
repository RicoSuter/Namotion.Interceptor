using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Namotion.Interceptor.Generator.Models;

namespace Namotion.Interceptor.Generator;

internal static class SubjectMethodMetadataExtractor
{
    private const string InterceptedMethodPostfix = "WithoutInterceptor";

    public static IReadOnlyList<MethodMetadata> CollectMethods(
        INamedTypeSymbol typeSymbol,
        SemanticModel semanticModel,
        Location location,
        List<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var methods = new List<MethodMetadata>();

        foreach (var syntaxReference in typeSymbol.DeclaringSyntaxReferences)
        {
            var declaration = syntaxReference.GetSyntax(cancellationToken);
            if (declaration is not TypeDeclarationSyntax typeDeclarationSyntax)
            {
                continue;
            }

            var declarationModel = semanticModel.Compilation.GetSemanticModel(typeDeclarationSyntax.SyntaxTree);

            foreach (var method in typeDeclarationSyntax.Members.OfType<MethodDeclarationSyntax>())
            {
                var metadata = ExtractMethod(typeSymbol, method, declarationModel, location, diagnostics);
                if (metadata is not null)
                {
                    methods.Add(metadata);
                }
            }
        }

        return methods;
    }

    private static MethodMetadata? ExtractMethod(
        INamedTypeSymbol typeSymbol, MethodDeclarationSyntax method, SemanticModel declarationModel,
        Location location, List<Diagnostic> diagnostics)
    {
        var fullMethodName = method.Identifier.Text;
        if (!fullMethodName.EndsWith(InterceptedMethodPostfix))
        {
            return null;
        }

        // The postfix is an explicit opt-in to interception, so a method that carries it and
        // still gets dropped is worth reporting: the user asked for a wrapper and silently
        // did not get one.

        // A method named exactly "WithoutInterceptor" would yield an empty wrapper name.
        if (fullMethodName.Length == InterceptedMethodPostfix.Length)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.MemberSkipped, location,
                $"{typeSymbol.Name}.{fullMethodName}",
                $"the name has no prefix before '{InterceptedMethodPostfix}'",
                "rename it so a name remains once the postfix is stripped"));
            return null;
        }

        // The emitter drops static and generic shapes, and cannot route an explicit interface
        // implementation through the executor. The wrapper forwards its parameters by value,
        // which a plain "ref" or an "out" parameter rejects (CS1620), while "in" and
        // "ref readonly" accept it, so only the first two are skipped. A by-reference return
        // type is skipped outright: GetFullTypeName cannot name a RefTypeSyntax, and the
        // wrapper would otherwise compile with a "void" return that silently dereferences the
        // ref return into a copy and discards it.
        if (HasUnsupportedDeclarationShape(method) || HasUnsupportedByReferenceShape(method))
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.MemberSkipped, location,
                $"{typeSymbol.Name}.{fullMethodName}",
                "the method shape is not supported (static, generic, a by-reference parameter other than 'in' or 'ref readonly', a by-reference return type, or an explicit interface implementation)",
                $"change the method shape, or drop the '{InterceptedMethodPostfix}' postfix if it should not be intercepted"));
            return null;
        }

        var methodName = fullMethodName.Substring(0, fullMethodName.Length - InterceptedMethodPostfix.Length);

        // The capture is silent: in derived mode the only compiler signal is a CS0108 that a
        // consumer without TreatWarningsAsErrors never sees, and an AddProperties wrapper
        // produces none at all. NI0063 scans declared members rather than emitted ones.
        if (GeneratedMemberTable.CollidesWithGeneratedMember(methodName))
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.MemberSkipped, location,
                $"{typeSymbol.Name}.{fullMethodName}",
                $"the wrapper would be named '{methodName}', which is a generated subject interception member",
                "rename the method so that stripping the postfix leaves a name the generated interception members do not use"));
            return null;
        }

        var returnType = GetFullTypeName(method.ReturnType, declarationModel);

        var parameterDeclarations = method.ParameterList.Parameters;
        var parameters = parameterDeclarations.Count == 0
            ? Array.Empty<ParameterMetadata>()
            : new ParameterMetadata[parameterDeclarations.Count];
        for (var index = 0; index < parameterDeclarations.Count; index++)
        {
            var parameter = parameterDeclarations[index];
            parameters[index] = new ParameterMetadata(
                parameter.Identifier.Text,
                GetFullTypeName(parameter.Type, declarationModel) ?? "object");
        }

        return new MethodMetadata(
            methodName,
            fullMethodName,
            returnType ?? "void",
            parameters);
    }

    private static bool HasUnsupportedDeclarationShape(MethodDeclarationSyntax method)
    {
        return method.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword)) ||
               method.TypeParameterList is not null ||
               method.ExplicitInterfaceSpecifier is not null;
    }

    private static bool HasUnsupportedByReferenceShape(MethodDeclarationSyntax method)
    {
        return method.ReturnType is RefTypeSyntax ||
               method.ParameterList.Parameters.Any(HasUnsupportedByReferenceModifier);
    }

    /// <summary>
    /// A "ref readonly" parameter carries both the "ref" and the "readonly" modifier, and unlike a
    /// plain "ref" it binds to the temporary the wrapper forwards, which only warns with CS9193.
    /// The generated file suppresses that warning, so only plain "ref" and "out" remain unsupported.
    /// </summary>
    private static bool HasUnsupportedByReferenceModifier(ParameterSyntax parameter)
    {
        var modifiers = parameter.Modifiers;
        return modifiers.Any(SyntaxKind.OutKeyword) ||
               (modifiers.Any(SyntaxKind.RefKeyword) && !modifiers.Any(SyntaxKind.ReadOnlyKeyword));
    }

    /// <summary>
    /// Names the type exactly as the property path does. A hand-built generic name of the form
    /// "{ContainingNamespace}.{Name}&lt;...&gt;" drops every enclosing type and renders the global
    /// namespace as the literal "&lt;global namespace&gt;", which does not parse; the fully
    /// qualified format handles both, so there is nothing left to special-case for generics.
    /// </summary>
    private static string? GetFullTypeName(TypeSyntax? type, SemanticModel semanticModel)
    {
        if (type == null)
        {
            return null;
        }

        return semanticModel.GetTypeInfo(type).Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }
}
