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

        // Keyed by simple name, valued by the member that took it, so a collision can name the
        // winner instead of leaving several identical warnings at one location.
        var winnerByPropertyName = new Dictionary<string, string>();
        HashSet<ISymbol>? processedSlots = null;

        foreach (var interfaceType in typeSymbol.AllInterfaces)
        {
            foreach (var member in interfaceType.GetMembers())
            {
                if (member is not IPropertySymbol property || !IsEligibleProperty(property))
                {
                    continue;
                }

                var (explicitImplementation, resolvedName, accessorInterface) = ResolvePropertySlot(property, interfaceType);

                if (!TryClaimSlot((explicitImplementation ?? property, resolvedName, accessorInterface), classPropertyNames,
                    winnerByPropertyName, ref processedSlots, location, diagnostics))
                {
                    continue;
                }

                var accessors = GetAccessibleAccessors(property, explicitImplementation, typeSymbol, accessorInterface, compilation);

                if (!HasEmittableAccessors(property, explicitImplementation, accessors, resolvedName, location, diagnostics))
                {
                    continue;
                }

                winnerByPropertyName[resolvedName] = $"{accessorInterface.ToDisplayString()}.{resolvedName}";

                interfaceProperties.Add(CreateMetadata(property, resolvedName, accessorInterface, accessors));
            }
        }

        return interfaceProperties;
    }

    private static (IPropertySymbol? ExplicitImplementation, string Name, INamedTypeSymbol AccessorInterface) ResolvePropertySlot(
        IPropertySymbol property, INamedTypeSymbol interfaceType)
    {
        // For an explicit implementation, IPropertySymbol.Name is the fully qualified
        // "Namespace.IHuman.Gender". The implemented member carries the simple name, and
        // its containing type is the interface the accessor must cast through: reflection
        // on the declaring interface does not find the member, and the implemented one
        // dispatches correctly in every direction.
        var explicitImplementation = property.ExplicitInterfaceImplementations.FirstOrDefault();
        var resolvedName = explicitImplementation?.Name ?? property.Name;
        var accessorInterface = explicitImplementation?.ContainingType ?? interfaceType;
        return (explicitImplementation, resolvedName, accessorInterface);
    }

    private static bool IsEligibleProperty(IPropertySymbol property)
    {
        // A property has a default implementation if any accessor is not abstract. This
        // runs before every other guard so that the guards below only ever fire on a
        // member the subject could plausibly have adopted: an abstract interface member is
        // implemented by the class itself, so nothing about it is skipped and reporting on
        // it would put a warning on every interface a subject implements.
        var hasDefaultImplementation =
            property.GetMethod is { IsAbstract: false } ||
            property.SetMethod is { IsAbstract: false };
        if (!hasDefaultImplementation)
        {
            return false;
        }

        // A static property with a body is not abstract, so it passes the default
        // implementation test above, but it cannot be read from an instance.
        if (SymbolExtensions.IsNeverASubjectProperty(property))
        {
            return false;
        }

        return true;
    }

    private static bool TryClaimSlot(
        (IPropertySymbol member, string resolvedName, INamedTypeSymbol accessorInterface) slot,
        HashSet<string> classPropertyNames, Dictionary<string, string> winnerByPropertyName,
        ref HashSet<ISymbol>? processedSlots, Location location, List<Diagnostic> diagnostics)
    {
        var (member, resolvedName, accessorInterface) = slot;
        // Skip properties already declared in the class. The class declaration is the
        // implementation, so nothing diverges and nothing is reported.
        if (classPropertyNames.Contains(resolvedName))
        {
            return false;
        }

        // Explicit interface overrides and their declarations share one dispatch slot.
        // AllInterfaces visits derived interfaces first, so retain the most-derived entry.
        if (!(processedSlots ??= new HashSet<ISymbol>(SymbolEqualityComparer.Default)).Add(member))
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

    private static (bool hasGetter, bool hasSetter, bool hasInit) GetAccessibleAccessors(
        IPropertySymbol property, IPropertySymbol? explicitImplementation, INamedTypeSymbol typeSymbol,
        INamedTypeSymbol accessorInterface, Compilation compilation)
    {
        // Roslyn reports an explicit implementation as Private regardless of the implemented
        // member's real visibility, so the accessibility that matters is the implemented
        // member's. Ask the compiler directly whether generated code (living inside
        // typeSymbol, accessing the member through a cast to accessorInterface) can reach
        // it, instead of hand-rolling the rule: a hardcoded "same assembly" premise breaks
        // as soon as the interface lives in a referenced assembly (internal and protected
        // internal members are then unreachable, CS0122/CS1540, unless InternalsVisibleTo
        // says otherwise), and passing accessorInterface as the qualifying type correctly
        // rejects protected members, which are never reachable through this cast pattern.
        // This runs after the cheap name-based filters above so the compiler is not asked
        // about a member that would be discarded anyway.
        var accessibilityMember = explicitImplementation ?? property;
        var (isGetterAccessible, isSetterAccessible) = SymbolExtensions.GetAccessorAccessibility(
            compilation, accessibilityMember, typeSymbol, accessorInterface);

        var hasGetter = property.GetMethod != null && isGetterAccessible;
        var hasSetter = property.SetMethod is { IsInitOnly: false } && isSetterAccessible;
        var hasInit = property.SetMethod?.IsInitOnly == true && isSetterAccessible;

        return (hasGetter, hasSetter, hasInit);
    }

    private static bool HasEmittableAccessors(
        IPropertySymbol property, IPropertySymbol? explicitImplementation,
        (bool hasGetter, bool hasSetter, bool hasInit) accessors,
        string resolvedName, Location location, List<Diagnostic> diagnostics)
    {
        var (hasGetter, hasSetter, _) = accessors;
        // Asked of the accessors that can actually be emitted, not of raw accessibility. An
        // init accessor is accessible but cannot be called from the emitted lambda, so a
        // property whose only reachable accessor is init would produce an entry with two null
        // accessors: a key that exists and does nothing. HasInit does not rescue it, being
        // consulted only when emitting a partial property's own accessor, which an interface
        // default never is. Skipped in silence, unlike the class-declared explicit
        // implementation in CollectProperties: an interface member generated code cannot see
        // was scoped as a helper by its own author rather than offered as a property, and the
        // interface may well be third-party, leaving the subject author with no remedy.
        if (!hasGetter && !hasSetter)
        {
            return false;
        }

        // The emitted metadata reflects the implemented member's PropertyInfo, not the
        // explicit implementation's, so anything declared on the implementation (a Derived
        // or validation attribute in particular) never reaches the runtime.
        if (explicitImplementation is not null && property.GetAttributes().Length > 0)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.ExplicitImplementationAttributesIgnored, location, resolvedName));
        }

        return true;
    }

    private static PropertyMetadata CreateMetadata(
        IPropertySymbol property, string resolvedName, INamedTypeSymbol accessorInterface,
        (bool hasGetter, bool hasSetter, bool hasInit) accessors)
    {
        var (hasGetter, hasSetter, hasInit) = accessors;
        var fullyQualifiedTypeName = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var accessModifier = SymbolExtensions.GetAccessModifierFromAccessibility(property.DeclaredAccessibility);
        var interfaceTypeName = accessorInterface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Interface default properties cannot be partial, virtual is implicit
        return new PropertyMetadata(
            resolvedName,
            fullyQualifiedTypeName,
            accessModifier,
            IsPartial: false,
            IsVirtual: true,  // Interface default implementations are implicitly virtual
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
