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

        // The postfix opts into interception, so report unsupported methods instead of silently skipping them.
        if (fullMethodName.Length == InterceptedMethodPostfix.Length)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.MemberSkipped, location,
                $"{typeSymbol.Name}.{fullMethodName}",
                $"the name has no prefix before '{InterceptedMethodPostfix}'",
                "rename it so a name remains once the postfix is stripped"));
            return null;
        }

        // Wrappers forward parameters by value. Ref returns cannot be named by GetFullTypeName
        // and would otherwise fall back to void, silently discarding the result.
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

        // NI0063 checks declared members, so wrapper-name collisions need a separate check.
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
    /// Returns whether the parameter requires ref or out forwarding.
    /// </summary>
    private static bool HasUnsupportedByReferenceModifier(ParameterSyntax parameter)
    {
        // Ref readonly accepts the forwarded temporary; generated code suppresses its CS9193 warning.
        var modifiers = parameter.Modifiers;
        return modifiers.Any(SyntaxKind.OutKeyword) ||
               (modifiers.Any(SyntaxKind.RefKeyword) && !modifiers.Any(SyntaxKind.ReadOnlyKeyword));
    }

    /// <summary>
    /// Returns the fully qualified type name, or null if the syntax or type cannot be resolved.
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
