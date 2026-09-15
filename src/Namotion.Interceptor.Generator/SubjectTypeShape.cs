using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Namotion.Interceptor.Generator.Models;

namespace Namotion.Interceptor.Generator;

internal static class SubjectTypeShape
{
    public static Diagnostic? Validate(INamedTypeSymbol typeSymbol, TypeDeclarationSyntax typeDeclaration, Location location)
    {
        // Report unsupported shapes before problems fixable by adding modifiers.
        if (typeDeclaration is not ClassDeclarationSyntax)
        {
            return Diagnostic.Create(
                Diagnostics.UnsupportedTypeKind, location,
                typeSymbol.Name, typeDeclaration.Keyword.ValueText);
        }

        if (typeDeclaration.Modifiers.Any(m => m.IsKind(SyntaxKind.FileKeyword)))
        {
            return Diagnostic.Create(Diagnostics.FileTypeNotSupported, location, typeSymbol.Name);
        }

        // IsGenericType also includes non-generic types nested in generic types; Arity identifies the subject itself.
        if (typeSymbol.Arity > 0)
        {
            return Diagnostic.Create(Diagnostics.GenericTypeNotSupported, location, typeSymbol.Name);
        }

        for (var parent = typeDeclaration.Parent; parent is TypeDeclarationSyntax outer; parent = parent.Parent)
        {
            if (outer.TypeParameterList is not null)
            {
                return Diagnostic.Create(
                    Diagnostics.GenericContainingTypeNotSupported, location,
                    typeSymbol.Name, outer.Identifier.ValueText);
            }
        }

        if (!typeDeclaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
        {
            return Diagnostic.Create(Diagnostics.NotPartial, location, typeSymbol.Name);
        }

        for (var parent = typeDeclaration.Parent; parent is TypeDeclarationSyntax outer; parent = parent.Parent)
        {
            if (!outer.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
            {
                return Diagnostic.Create(
                    Diagnostics.ContainingTypeNotPartial, location,
                    outer.Identifier.ValueText, typeSymbol.Name);
            }
        }

        return null;
    }

    public static string? GetNamespace(TypeDeclarationSyntax typeDeclaration)
    {
        SyntaxNode? current = typeDeclaration.Parent;
        while (current is TypeDeclarationSyntax)
        {
            current = current.Parent;
        }

        // null means the global namespace: the generated file must not declare one.
        return (current as NamespaceDeclarationSyntax)?.Name.ToString() ??
               (current as FileScopedNamespaceDeclarationSyntax)?.Name.ToString();
    }

    public static ContainingType[] GetContainingTypes(SyntaxNode node)
    {
        var types = new List<ContainingType>();
        var parent = node.Parent;
        while (parent is TypeDeclarationSyntax typeDeclaration)
        {
            types.Insert(0, new ContainingType(
                GetTypeKeyword(typeDeclaration),
                typeDeclaration.Identifier.Text));
            parent = parent.Parent;
        }
        return types.ToArray();
    }

    /// <summary>
    /// Returns the declaration keyword, preserving the class or struct qualifier for records.
    /// </summary>
    private static string GetTypeKeyword(TypeDeclarationSyntax typeDeclaration)
    {
        if (typeDeclaration is not RecordDeclarationSyntax recordDeclaration)
        {
            return typeDeclaration.Keyword.ValueText;
        }

        var classOrStructKeyword = recordDeclaration.ClassOrStructKeyword.ValueText;
        return string.IsNullOrEmpty(classOrStructKeyword)
            ? recordDeclaration.Keyword.ValueText
            : $"{recordDeclaration.Keyword.ValueText} {classOrStructKeyword}";
    }

    /// <summary>
    /// Determines parameterless constructor generation, availability, and required-member attribute requirements.
    /// </summary>
    public static (bool NeedsGeneratedParameterlessConstructor, bool HasOrWillHaveParameterlessConstructor, bool ParameterlessConstructorSetsRequiredMembers) DetectConstructorState(
        INamedTypeSymbol typeSymbol,
        TypeDeclarationSyntax[] allTypeDeclarations)
    {
        // Static constructors cannot satisfy emitted constructor chaining.
        var firstConstructor = allTypeDeclarations
            .SelectMany(c => c.Members)
            .OfType<ConstructorDeclarationSyntax>()
            .FirstOrDefault(constructor => !constructor.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword)));

        // Chaining eligibility currently depends on the first declared instance constructor.
        var needsGeneratedParameterlessConstructor = firstConstructor is null;
        var hasOrWillHaveParameterlessConstructor = firstConstructor is null or { ParameterList.Parameters.Count: 0 };

        return (
            needsGeneratedParameterlessConstructor,
            hasOrWillHaveParameterlessConstructor,
            hasOrWillHaveParameterlessConstructor && ChainedConstructorSetsRequiredMembers(typeSymbol));
    }

    /// <summary>
    /// Determines whether emitted constructors must propagate [SetsRequiredMembers] from their constructor chain.
    /// </summary>
    private static bool ChainedConstructorSetsRequiredMembers(INamedTypeSymbol typeSymbol)
    {
        for (var type = typeSymbol; type is not null; type = type.BaseType)
        {
            var constructor = type.InstanceConstructors.FirstOrDefault(candidate => candidate.Parameters.Length == 0);
            if (constructor is null)
            {
                return false;
            }

            // Implicit subject constructors will inherit the base constructor's attribute.
            // Other constructors already expose their final attribute state.
            if (!constructor.IsImplicitlyDeclared)
            {
                return constructor.GetAttributes().Any(attribute =>
                    SymbolExtensions.IsTypeOrInheritsFrom(attribute.AttributeClass, KnownTypes.SetsRequiredMembersAttribute));
            }

            // Generated constructors cannot claim to initialize required members declared by this type.
            if (type.GetMembers().Any(member => member is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true }))
            {
                return false;
            }
        }

        return false;
    }
}
