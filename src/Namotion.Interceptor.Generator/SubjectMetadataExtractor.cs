using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Namotion.Interceptor.Generator.Models;

namespace Namotion.Interceptor.Generator;

internal static class SubjectMetadataExtractor
{
    /// <summary>
    /// Extracts metadata from a type declaration with the InterceptorSubject attribute.
    /// </summary>
    public static ExtractionResult Extract(
        INamedTypeSymbol typeSymbol,
        TypeDeclarationSyntax typeDeclaration,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<Diagnostic>();

        var location = typeDeclaration.Identifier.GetLocation();

        var shapeDiagnostic = SubjectTypeShape.Validate(typeSymbol, typeDeclaration, location);
        if (shapeDiagnostic is not null)
        {
            diagnostics.Add(shapeDiagnostic);
            return new ExtractionResult(null, diagnostics);
        }

        // Preserve '@' for keyword class names. Property metadata uses ValueText because its
        // names also become string literals and derived identifiers such as OnNameChanged.
        var className = typeDeclaration.Identifier.Text;

        // Symbol accessibility includes the different top-level and nested defaults.
        var accessModifier = SymbolExtensions.GetAccessModifierFromAccessibility(typeSymbol.DeclaredAccessibility);

        // Sealed may appear on another partial declaration.
        var isSealed = typeSymbol.IsSealed;

        var containingTypes = SubjectTypeShape.GetContainingTypes(typeDeclaration);
        var namespaceName = SubjectTypeShape.GetNamespace(typeDeclaration);
        var fullTypeName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        var baseClass = SubjectBaseContract.Resolve(
            typeSymbol, semanticModel.Compilation, location, diagnostics, cancellationToken);

        if (baseClass is null)
        {
            return new ExtractionResult(null, diagnostics);
        }

        var allTypeDeclarations = typeSymbol.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax(cancellationToken))
            .OfType<TypeDeclarationSyntax>()
            .ToArray();

        var classProperties = ClassPropertyMetadataExtractor.DeduplicateByName(
            ClassPropertyMetadataExtractor.CollectProperties(typeSymbol, semanticModel, location, diagnostics, cancellationToken),
            typeSymbol.ToDisplayString(),
            location,
            diagnostics);

        // NI0065 takes precedence over NI0060 when both match the same declaration.
        var displacedNames = SubjectPropertyConflicts.ReportPropertiesDisplacingAnAncestorSubject(
            typeSymbol, classProperties, location, diagnostics);

        SubjectPropertyConflicts.ReportPropertiesShadowingABaseImplementation(
            typeSymbol, classProperties, displacedNames, location, diagnostics);

        var interfaceProperties = InterfacePropertyMetadataExtractor.ExtractInterfaceDefaultProperties(
            typeSymbol, classProperties, semanticModel.Compilation, location, diagnostics);

        var properties = classProperties.Concat(interfaceProperties).ToList();

        var methods = SubjectMethodMetadataExtractor.CollectMethods(typeSymbol, semanticModel, location, diagnostics, cancellationToken);

        var (needsGeneratedParameterlessConstructor, hasOrWillHaveParameterlessConstructor,
            parameterlessConstructorSetsRequiredMembers) = SubjectTypeShape.DetectConstructorState(typeSymbol, allTypeDeclarations);

        return new ExtractionResult(
            new SubjectMetadata(
                className,
                accessModifier,
                isSealed,
                namespaceName,
                fullTypeName,
                containingTypes,
                needsGeneratedParameterlessConstructor,
                hasOrWillHaveParameterlessConstructor,
                parameterlessConstructorSetsRequiredMembers,
                baseClass,
                properties,
                methods),
            diagnostics);
    }
}
