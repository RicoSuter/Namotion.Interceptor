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

        // Resolved once and reused below for the explicit-implementation accessibility
        // check: an indexer cannot reach this path (it parses as IndexerDeclarationSyntax,
        // which CollectProperties never passes here), but a static property can, and the
        // same rule that skips one on the interface-default path must skip it here too,
        // or it emits a cast-through-the-class accessor that fails CS0176.
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
    /// Narrows a class-declared explicit implementation's accessors to the ones generated code can
    /// reach through the implemented member. Returns false, having reported why, when the declaration
    /// does not become a subject property.
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
        // Reported, unlike the same outcome on the interface-default path: writing
        // an explicit implementation on the subject itself is an opt-in to it
        // becoming a subject property, in the author's own file, and silently
        // dropping it would leave them with no way to find out.
        if (!isGetterAccessible && !isSetterAccessible)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.MemberSkipped, location,
                $"{typeSymbol.Name}.{implementedMember.ContainingType.Name}.{implementedMember.Name}",
                "the member is not accessible from generated code",
                "declare an accessor the subject's generated half can reach"));
            return false;
        }

        // The emitted metadata reflects the interface member's PropertyInfo, not
        // this declaration's, so anything declared here never reaches the runtime.
        if (property.AttributeLists.Count > 0)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.ExplicitImplementationAttributesIgnored, location, property.Identifier.ValueText));
        }

        accessors.HasGetter &= isGetterAccessible;
        accessors.HasSetter &= isSetterAccessible;
        accessors.HasInit &= isSetterAccessible;

        // Narrowing can leave both emittable accessors off while HasInit survives,
        // which would add a key whose getter and setter lambdas are both null. Same
        // outcome as the inaccessible case above, so it is reported the same way.
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
    /// Whether an override that leaves the accessor out still exposes the inherited one to generated
    /// code, which then emits a lambda for it.
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
    /// Two declarations can share a name when a class declares a property and also explicitly
    /// implements the same interface member. Emitting both produces duplicate dictionary keys,
    /// so the non-explicit declaration wins, matching what the runtime resolves.
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
        // Reported only once every declaration has been seen, because the winner the message names
        // is not knowable mid-loop: a class-declared property takes the name whether it is written
        // before or after the explicit implementations. Iterating the deduplicated result rather
        // than the dictionary keeps the diagnostic order deterministic.
        foreach (var winner in result)
        {
            // Two explicit implementations of one simple name (typically one generic interface at
            // two instantiations) is the class-declared form of the NI0061 collision: whatever
            // claims the name, at least one interface member is dropped. A class property colliding
            // with a single explicit implementation is not: only one of the two comes from an
            // interface, and the class property is the documented winner.
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
    /// Names an explicitly implemented member the way a compiler error message would, so the
    /// "global::" the emitter needs in generated code does not leak into a diagnostic.
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
