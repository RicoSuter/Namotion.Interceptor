using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Namotion.Interceptor.Generator.Models;

namespace Namotion.Interceptor.Generator;

internal static class ClassPropertyMetadataExtractor
{
    public static IReadOnlyList<PropertyMetadata> CollectProperties(
        INamedTypeSymbol typeSymbol,
        SemanticModel semanticModel,
        Location location,
        List<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var properties = new List<PropertyMetadata>();

        foreach (var syntaxReference in typeSymbol.DeclaringSyntaxReferences)
        {
            var declaration = syntaxReference.GetSyntax(cancellationToken);
            if (declaration is not TypeDeclarationSyntax typeDeclarationSyntax)
            {
                continue;
            }

            var declarationModel = semanticModel.Compilation.GetSemanticModel(typeDeclarationSyntax.SyntaxTree);

            foreach (var property in typeDeclarationSyntax.Members.OfType<PropertyDeclarationSyntax>())
            {
                var metadata = ExtractProperty(typeSymbol, property, declarationModel, location, diagnostics, cancellationToken);
                if (metadata is not null)
                {
                    properties.Add(metadata);
                }
            }
        }

        return properties;
    }

    private static PropertyMetadata? ExtractProperty(
        INamedTypeSymbol typeSymbol, PropertyDeclarationSyntax property, SemanticModel declarationModel,
        Location location, List<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var typeInfo = declarationModel.GetTypeInfo(property.Type, cancellationToken);
        var fullyQualifiedName = typeInfo.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "object";

        // Static properties also reach this path; instance access would fail with CS0176.
        var declaredPropertySymbol = declarationModel.GetDeclaredSymbol(property, cancellationToken);
        if (declaredPropertySymbol is not null && SymbolExtensions.IsNeverASubjectProperty(declaredPropertySymbol))
        {
            return null;
        }

        var explicitInterfaceTypeName = GetExplicitInterfaceTypeName(property, declarationModel, cancellationToken);
        var modifiers = property.Modifiers;
        var isOverride = modifiers.Any(m => m.IsKind(SyntaxKind.OverrideKeyword));
        var isDerived = HasDerivedAttribute(property, declarationModel, cancellationToken);
        var accessors = GetDeclaredAccessors(property);

        if (property.ExplicitInterfaceSpecifier is not null &&
            !NarrowExplicitAccessors(property, declaredPropertySymbol, declarationModel.Compilation, typeSymbol, location, diagnostics, ref accessors))
        {
            return null;
        }

        return new PropertyMetadata(
            property.Identifier.ValueText,
            fullyQualifiedName,
            GetAccessModifier(modifiers),
            IsPartial: modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)) && property.ExplicitInterfaceSpecifier is null,
            IsVirtual: modifiers.Any(m => m.IsKind(SyntaxKind.VirtualKeyword)),
            IsOverride: isOverride,
            IsNew: modifiers.Any(m => m.IsKind(SyntaxKind.NewKeyword)),
            IsSealed: modifiers.Any(m => m.IsKind(SyntaxKind.SealedKeyword)),
            IsDerived: isDerived,
            IsRequired: modifiers.Any(m => m.IsKind(SyntaxKind.RequiredKeyword)),
            accessors.HasGetter,
            accessors.HasSetter,
            accessors.HasInit,
            IsFromInterface: false,
            GetAccessorModifier(property.AccessorList, SyntaxKind.GetAccessorDeclaration),
            GetAccessorModifier(property.AccessorList, SyntaxKind.SetAccessorDeclaration) ??
                GetAccessorModifier(property.AccessorList, SyntaxKind.InitAccessorDeclaration),
            InterfaceTypeName: null,
            ExplicitInterfaceTypeName: explicitInterfaceTypeName,
            HasInheritedGetter: HasAccessibleInheritedAccessor(
                declaredPropertySymbol, isOverride, declaresAccessor: accessors.HasGetter, isGetter: true, declarationModel.Compilation, typeSymbol),
            HasInheritedSetter: HasAccessibleInheritedAccessor(
                declaredPropertySymbol, isOverride, declaresAccessor: accessors.HasSetter || accessors.HasInit, isGetter: false, declarationModel.Compilation, typeSymbol));
    }

    private static string? GetExplicitInterfaceTypeName(
        PropertyDeclarationSyntax property, SemanticModel declarationModel, CancellationToken cancellationToken)
    {
        return property.ExplicitInterfaceSpecifier is { } explicitSpecifier
            ? declarationModel
                .GetTypeInfo(explicitSpecifier.Name, cancellationToken)
                .Type?
                .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            : null;
    }

    private static (bool HasGetter, bool HasSetter, bool HasInit) GetDeclaredAccessors(PropertyDeclarationSyntax property)
    {
        var hasGetter = property.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) == true ||
                        property.ExpressionBody != null;
        var hasSetter = property.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) == true;
        var hasInit = property.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.InitAccessorDeclaration)) == true;

        return (hasGetter, hasSetter, hasInit);
    }

    /// <summary>
    /// Retains reachable explicit accessors; reports a diagnostic and returns false if none can be emitted.
    /// </summary>
    private static bool NarrowExplicitAccessors(
        PropertyDeclarationSyntax property, IPropertySymbol? declaredPropertySymbol, Compilation compilation,
        INamedTypeSymbol typeSymbol, Location location, List<Diagnostic> diagnostics,
        ref (bool HasGetter, bool HasSetter, bool HasInit) accessors)
    {
        var implementedMember = declaredPropertySymbol?.ExplicitInterfaceImplementations.FirstOrDefault();
        if (implementedMember is null)
        {
            return true;
        }

        var (isGetterAccessible, isSetterAccessible) = SymbolExtensions.GetAccessorAccessibility(
            compilation, implementedMember, typeSymbol, implementedMember.ContainingType);
        // A class-declared implementation opts into a subject property, so report why it is skipped.
        if (!isGetterAccessible && !isSetterAccessible)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.MemberSkipped, location,
                $"{typeSymbol.Name}.{implementedMember.ContainingType.Name}.{implementedMember.Name}",
                "the member is not accessible from generated code",
                "declare an accessor the subject's generated half can reach"));
            return false;
        }

        // Runtime metadata uses the interface member's PropertyInfo, omitting attributes declared here.
        if (property.AttributeLists.Count > 0)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.ExplicitImplementationAttributesIgnored, location, property.Identifier.ValueText));
        }

        accessors.HasGetter &= isGetterAccessible;
        accessors.HasSetter &= isSetterAccessible;
        accessors.HasInit &= isSetterAccessible;

        // An init-only survivor cannot supply either runtime accessor lambda.
        if (!accessors.HasGetter && !accessors.HasSetter)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.MemberSkipped, location,
                $"{typeSymbol.Name}.{implementedMember.ContainingType.Name}.{implementedMember.Name}",
                "no accessor the generated code can emit remains",
                "declare a get or set accessor the subject's generated half can reach"));
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns whether an omitted override accessor is inherited and accessible to generated code.
    /// </summary>
    private static bool HasAccessibleInheritedAccessor(
        IPropertySymbol? declaredPropertySymbol, bool isOverride, bool declaresAccessor, bool isGetter,
        Compilation compilation, INamedTypeSymbol subjectType)
    {
        if (!isOverride || declaresAccessor)
        {
            return false;
        }

        // Follow the overridden slot, including plain intermediate classes. A same-named property
        // elsewhere in the base chain may belong to a different slot.
        for (var property = declaredPropertySymbol?.OverriddenProperty; property is not null; property = property.OverriddenProperty)
        {
            var accessor = isGetter ? property.GetMethod : property.SetMethod;
            if (accessor is not null)
            {
                return !accessor.IsInitOnly && compilation.IsSymbolAccessibleWithin(accessor, subjectType, subjectType);
            }
        }

        return false;
    }

    /// <summary>
    /// Selects one property per name, preferring non-explicit declarations to match runtime lookup.
    /// </summary>
    public static IReadOnlyList<PropertyMetadata> DeduplicateByName(
        IReadOnlyList<PropertyMetadata> properties,
        string subjectDisplayName,
        Location location,
        List<Diagnostic> diagnostics)
    {
        var result = new List<PropertyMetadata>();
        var indexByName = new Dictionary<string, int>();
        var explicitImplementationsByName = new Dictionary<string, List<PropertyMetadata>>();

        foreach (var property in properties)
        {
            if (property.ExplicitInterfaceTypeName is not null)
            {
                if (!explicitImplementationsByName.TryGetValue(property.Name, out var explicitImplementations))
                {
                    explicitImplementations = new List<PropertyMetadata>();
                    explicitImplementationsByName[property.Name] = explicitImplementations;
                }

                explicitImplementations.Add(property);
            }

            if (!indexByName.TryGetValue(property.Name, out var index))
            {
                indexByName[property.Name] = result.Count;
                result.Add(property);
                continue;
            }

            if (result[index].ExplicitInterfaceTypeName is not null &&
                property.ExplicitInterfaceTypeName is null)
            {
                result[index] = property;
            }
        }

        ReportExplicitImplementationCollisions(result, explicitImplementationsByName, subjectDisplayName, location, diagnostics);

        return result;
    }

    private static void ReportExplicitImplementationCollisions(
        List<PropertyMetadata> result, Dictionary<string, List<PropertyMetadata>> explicitImplementationsByName,
        string subjectDisplayName, Location location, List<Diagnostic> diagnostics)
    {
        // Winners can change until all declarations are seen. Result order keeps diagnostics deterministic.
        foreach (var winner in result)
        {
            // Multiple explicit implementations lose at least one interface member (NI0061).
            // A class property colliding with one explicit implementation is an expected winner.
            if (!explicitImplementationsByName.TryGetValue(winner.Name, out var explicitImplementations) ||
                explicitImplementations.Count < 2)
            {
                continue;
            }

            var winnerDescription = winner.ExplicitInterfaceTypeName is not null
                ? DescribeExplicitImplementation(winner)
                : $"the class property {subjectDisplayName}.{winner.Name}";

            foreach (var droppedProperty in explicitImplementations)
            {
                if (ReferenceEquals(droppedProperty, winner))
                {
                    continue;
                }

                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.PropertyNameCollision, location,
                    winner.Name, winnerDescription, DescribeExplicitImplementation(droppedProperty)));
            }
        }
    }

    /// <summary>
    /// Formats an explicit member for diagnostics without the global namespace qualifier.
    /// </summary>
    private static string DescribeExplicitImplementation(PropertyMetadata property)
    {
        const string globalPrefix = "global::";

        var interfaceTypeName = property.ExplicitInterfaceTypeName!;
        if (interfaceTypeName.StartsWith(globalPrefix))
        {
            interfaceTypeName = interfaceTypeName.Substring(globalPrefix.Length);
        }

        return $"{interfaceTypeName}.{property.Name}";
    }

    private static string GetAccessModifier(SyntaxTokenList modifiers)
    {
        var hasPublic = modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword));
        var hasProtected = modifiers.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword));
        var hasInternal = modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword));
        var hasPrivate = modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword));

        return (hasPublic, hasProtected, hasInternal, hasPrivate) switch
        {
            (true, _, _, _) => "public",
            (_, true, true, _) => "protected internal",
            (_, true, _, true) => "private protected",
            (_, true, _, _) => "protected",
            (_, _, true, _) => "internal",
            _ => "private"
        };
    }

    private static string? GetAccessorModifier(AccessorListSyntax? accessorList, SyntaxKind accessorKind)
    {
        var accessor = accessorList?.Accessors.FirstOrDefault(a => a.IsKind(accessorKind));
        if (accessor == null)
        {
            return null;
        }

        var modifiers = accessor.Modifiers;
        if (modifiers.Count == 0)
        {
            return null;
        }

        return string.Join(" ", modifiers.Select(m => m.ValueText));
    }

    private static bool HasDerivedAttribute(PropertyDeclarationSyntax property, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        return SymbolExtensions.HasAttribute(property.AttributeLists, KnownTypes.DerivedAttribute, semanticModel, cancellationToken);
    }
}
