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

        // Text rather than ValueText: ValueText drops the '@' that escapes a keyword used as a
        // name, and the bare keyword does not parse. Property names below stay on ValueText,
        // because they are also emitted as string literals, as nameof arguments and as parts of
        // derived names such as _Name and OnNameChanged, where the escape would be wrong.
        var className = typeDeclaration.Identifier.Text;

        // Use the symbol rather than the syntax modifiers: a top-level class without a modifier
        // defaults to internal, a nested one to private.
        var accessModifier = SymbolExtensions.GetAccessModifierFromAccessibility(typeSymbol.DeclaredAccessibility);

        // From the symbol, because 'sealed' may sit on any partial declaration, not necessarily
        // the attributed one. DetectConstructorState already scans every declaration for the same
        // reason.
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

        // Collect all partial type declarations
        var allTypeDeclarations = typeSymbol.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax(cancellationToken))
            .OfType<TypeDeclarationSyntax>()
            .ToArray();

        // Collect properties from all partial declarations
        var classProperties = ClassPropertyMetadataExtractor.DeduplicateByName(
            ClassPropertyMetadataExtractor.CollectProperties(typeSymbol, semanticModel, location, diagnostics, cancellationToken),
            typeSymbol.ToDisplayString(),
            location,
            diagnostics);

        // NI0065 runs first and its names are handed to NI0060, which stands down on them. The two
        // rules are not mutually exclusive and both can match one declaration.
        var displacedNames = SubjectPropertyConflicts.ReportPropertiesDisplacingAnAncestorSubject(
            typeSymbol, classProperties, location, diagnostics);

        SubjectPropertyConflicts.ReportPropertiesShadowingABaseImplementation(
            typeSymbol, classProperties, displacedNames, location, diagnostics);

        // Collect interface properties with default implementations
        var interfaceProperties = InterfacePropertyMetadataExtractor.ExtractInterfaceDefaultProperties(
            typeSymbol, classProperties, semanticModel.Compilation, location, diagnostics);

        // Combine class properties with interface default properties
        var properties = classProperties.Concat(interfaceProperties).ToList();

        // Collect methods from all partial declarations
        var methods = SubjectMethodMetadataExtractor.CollectMethods(typeSymbol, semanticModel, location, diagnostics, cancellationToken);

        // Detect constructor state
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
