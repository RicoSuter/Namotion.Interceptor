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
        // Guards run fundamentally-unsupported shapes before fixable ones, so a subject with more
        // than one problem points the user at the one they cannot work around by adding a modifier.
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

        // Arity, not IsGenericType: Roslyn reports IsGenericType = true for a non-generic type
        // nested inside a generic one, which would misname the subject here as generic when it is
        // really the containing type (checked below) that carries the type parameters.
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
        // Walk up past containing types to find namespace
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
    /// "record" alone is correct for a record class, because record defaults to a class, but a
    /// record struct needs both tokens or the partial declarations conflict.
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
    /// Detects the constructor state for the class.
    /// Returns a tuple of:
    /// - NeedsGeneratedParameterlessConstructor: true if no constructor exists and we need to generate one
    /// - HasOrWillHaveParameterlessConstructor: true if we have or will generate a parameterless constructor
    /// - ParameterlessConstructorSetsRequiredMembers: true if the emitted constructors have to carry [SetsRequiredMembers]
    /// </summary>
    public static (bool NeedsGeneratedParameterlessConstructor, bool HasOrWillHaveParameterlessConstructor, bool ParameterlessConstructorSetsRequiredMembers) DetectConstructorState(
        INamedTypeSymbol typeSymbol,
        TypeDeclarationSyntax[] allTypeDeclarations)
    {
        // A static constructor is not an instance constructor, so nothing can chain to it and it
        // never stands in for the parameterless one the emitted constructors need.
        var firstConstructor = allTypeDeclarations
            .SelectMany(c => c.Members)
            .OfType<ConstructorDeclarationSyntax>()
            .FirstOrDefault(constructor => !constructor.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword)));

        // No constructor at all: generate a parameterless one. A first constructor with parameters
        // means there is no parameterless one to chain to, so nothing is generated.
        var needsGeneratedParameterlessConstructor = firstConstructor is null;
        var hasOrWillHaveParameterlessConstructor = firstConstructor is null or { ParameterList.Parameters.Count: 0 };

        return (
            needsGeneratedParameterlessConstructor,
            hasOrWillHaveParameterlessConstructor,
            hasOrWillHaveParameterlessConstructor && ChainedConstructorSetsRequiredMembers(typeSymbol));
    }

    /// <summary>
    /// Whether the parameterless constructor the emitted constructors chain to carries
    /// [SetsRequiredMembers]. CS9039 rejects a constructor that chains to one with the attribute unless
    /// it repeats it, for the implicit base chain as much as for ": this()", so the emitted constructors
    /// mirror it. Read off symbols because the constructor may sit in another partial file than the one
    /// this extraction's semantic model belongs to.
    /// </summary>
    /// <remarks>
    /// The walk passes through implicitly declared constructors: on a subject the generator replaces
    /// each of those with an explicit one that mirrors its own base, and that emitted constructor is
    /// not visible in this compilation's symbols. Passing through a non-subject one is safe too,
    /// because an implicit constructor above an attributed one is already CS9039 in its own right.
    /// Constructors read from metadata are never implicit, so a referenced assembly ends the walk on
    /// its real attribute state. The walk stops at a type that declares required members of its own,
    /// because an emitted constructor claiming to set them would be lying; that subject keeps the
    /// CS9039 and has to declare the initializing constructor itself.
    /// </remarks>
    private static bool ChainedConstructorSetsRequiredMembers(INamedTypeSymbol typeSymbol)
    {
        for (var type = typeSymbol; type is not null; type = type.BaseType)
        {
            var constructor = type.InstanceConstructors.FirstOrDefault(candidate => candidate.Parameters.Length == 0);
            if (constructor is null)
            {
                return false;
            }

            if (!constructor.IsImplicitlyDeclared)
            {
                return constructor.GetAttributes().Any(attribute =>
                    SymbolExtensions.IsTypeOrInheritsFrom(attribute.AttributeClass, KnownTypes.SetsRequiredMembersAttribute));
            }

            if (type.GetMembers().Any(member => member is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true }))
            {
                return false;
            }
        }

        return false;
    }
}
