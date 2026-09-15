using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Namotion.Interceptor.Generator.Models;

namespace Namotion.Interceptor.Generator;

internal static class InterfacePropertyMetadataExtractor
{
    /// <summary>
    /// Extracts properties with default implementations from all interfaces implemented by the type.
    /// </summary>
    public static IReadOnlyList<PropertyMetadata> ExtractInterfaceDefaultProperties(
        INamedTypeSymbol typeSymbol,
        IReadOnlyList<PropertyMetadata> classProperties,
        Compilation compilation,
        Location location,
        List<Diagnostic> diagnostics)
    {
        var interfaceProperties = new List<PropertyMetadata>();
        var classPropertyNames = new HashSet<string>(classProperties.Select(p => p.Name));

        // Retain each winner's description to distinguish collision diagnostics.
        var winnerByPropertyName = new Dictionary<string, string>();
        HashSet<ISymbol>? processedSlots = null;

        foreach (var interfaceType in typeSymbol.AllInterfaces)
        {
            foreach (var member in interfaceType.GetMembers())
            {
                // Reject ineligible members before slot claims and collision diagnostics.
                if (member is not IPropertySymbol property || !IsEligibleProperty(property))
                {
                    continue;
                }

                var (explicitImplementation, resolvedName, accessorInterface) = ResolvePropertySlot(property, interfaceType);

                // Class declarations intentionally take precedence without a collision warning.
                if (classPropertyNames.Contains(resolvedName) ||
                    !TryClaimSlot(explicitImplementation ?? property, resolvedName, accessorInterface, winnerByPropertyName, ref processedSlots, location, diagnostics))
                {
                    continue;
                }

                // Defer semantic accessibility checks until the cheap name checks pass.
                if (!TryGetEmittableAccessors(property, explicitImplementation, typeSymbol, accessorInterface, compilation, out var accessors))
                {
                    continue;
                }

                ReportIgnoredExplicitImplementationAttributes(property, explicitImplementation, resolvedName, location, diagnostics);

                // Only an emittable member may block later members with the same name.
                winnerByPropertyName[resolvedName] = $"{accessorInterface.ToDisplayString()}.{resolvedName}";

                interfaceProperties.Add(CreateMetadata(property, resolvedName, accessorInterface, accessors));
            }
        }

        return interfaceProperties;
    }

    private static (IPropertySymbol? ExplicitImplementation, string Name, INamedTypeSymbol AccessorInterface) ResolvePropertySlot(
        IPropertySymbol property, INamedTypeSymbol interfaceType)
    {
        // The implemented member supplies the simple name and the interface required for
        // reflection and cast-based dispatch; the explicit declaration supplies neither.
        var explicitImplementation = property.ExplicitInterfaceImplementations.FirstOrDefault();
        var resolvedName = explicitImplementation?.Name ?? property.Name;
        var accessorInterface = explicitImplementation?.ContainingType ?? interfaceType;
        return (explicitImplementation, resolvedName, accessorInterface);
    }

    private static bool IsEligibleProperty(IPropertySymbol property)
    {
        // Abstract members are implemented by the class, so do not report them as skipped defaults.
        var hasDefaultImplementation =
            property.GetMethod is { IsAbstract: false } ||
            property.SetMethod is { IsAbstract: false };
        if (!hasDefaultImplementation)
        {
            return false;
        }

        // Static bodies pass the non-abstract check but cannot be accessed through an instance.
        if (SymbolExtensions.IsNeverASubjectProperty(property))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Claims a new dispatch slot without reserving its name; reports NI0061 if another slot owns the name.
    /// </summary>
    private static bool TryClaimSlot(
        IPropertySymbol slot, string resolvedName, INamedTypeSymbol accessorInterface,
        Dictionary<string, string> winnerByPropertyName, ref HashSet<ISymbol>? processedSlots,
        Location location, List<Diagnostic> diagnostics)
    {
        // Explicit interface overrides and their declarations share one dispatch slot.
        // AllInterfaces visits derived interfaces first, so retain the most-derived entry.
        if (!(processedSlots ??= new HashSet<ISymbol>(SymbolEqualityComparer.Default)).Add(slot))
        {
            return false;
        }

        // Distinct slots with the same simple name compete for one metadata entry.
        if (winnerByPropertyName.TryGetValue(resolvedName, out var winnerDescription))
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.PropertyNameCollision, location,
                resolvedName, winnerDescription, $"{accessorInterface.ToDisplayString()}.{resolvedName}"));
            return false;
        }

        return true;
    }

    private static bool TryGetEmittableAccessors(
        IPropertySymbol property, IPropertySymbol? explicitImplementation, INamedTypeSymbol typeSymbol,
        INamedTypeSymbol accessorInterface, Compilation compilation,
        out (bool HasGetter, bool HasSetter, bool HasInit) accessors)
    {
        // Explicit implementations appear private in Roslyn; check the implemented member instead.
        // The interface qualifier models the emitted cast, including protected and cross-assembly access.
        var accessibilityMember = explicitImplementation ?? property;
        var (isGetterAccessible, isSetterAccessible) = SymbolExtensions.GetAccessorAccessibility(
            compilation, accessibilityMember, typeSymbol, accessorInterface);

        var hasGetter = property.GetMethod != null && isGetterAccessible;
        var hasSetter = property.SetMethod is { IsInitOnly: false } && isSetterAccessible;
        var hasInit = property.SetMethod?.IsInitOnly == true && isSetterAccessible;
        accessors = (hasGetter, hasSetter, hasInit);

        // Init cannot supply a runtime accessor lambda. Silently skip defaults with no emittable
        // accessor: they may be private helpers in interfaces the subject author cannot change.
        return hasGetter || hasSetter;
    }

    private static void ReportIgnoredExplicitImplementationAttributes(
        IPropertySymbol property, IPropertySymbol? explicitImplementation, string resolvedName,
        Location location, List<Diagnostic> diagnostics)
    {
        // Runtime metadata uses the implemented member's PropertyInfo, omitting implementation attributes.
        if (explicitImplementation is not null && property.GetAttributes().Length > 0)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.ExplicitImplementationAttributesIgnored, location, resolvedName));
        }
    }

    private static PropertyMetadata CreateMetadata(
        IPropertySymbol property, string resolvedName, INamedTypeSymbol accessorInterface,
        (bool HasGetter, bool HasSetter, bool HasInit) accessors)
    {
        var (hasGetter, hasSetter, hasInit) = accessors;
        var fullyQualifiedTypeName = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var accessModifier = SymbolExtensions.GetAccessModifierFromAccessibility(property.DeclaredAccessibility);
        var interfaceTypeName = accessorInterface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        return new PropertyMetadata(
            resolvedName,
            fullyQualifiedTypeName,
            accessModifier,
            IsPartial: false,
            IsVirtual: true,  // Interface defaults are implicitly virtual.
            IsOverride: false,
            IsNew: false,
            IsSealed: false,
            IsDerived: HasDerivedAttribute(property),
            IsRequired: false,
            hasGetter,
            hasSetter,
            hasInit,
            IsFromInterface: true,
            GetterAccessModifier: null,
            SetterAccessModifier: null,
            InterfaceTypeName: interfaceTypeName);
    }

    private static bool HasDerivedAttribute(IPropertySymbol property)
    {
        return property.GetAttributes()
            .Any(a => SymbolExtensions.IsTypeOrInheritsFrom(a.AttributeClass, KnownTypes.DerivedAttribute));
    }
}
