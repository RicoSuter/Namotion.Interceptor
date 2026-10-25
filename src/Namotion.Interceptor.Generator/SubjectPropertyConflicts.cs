using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Namotion.Interceptor.Generator.Models;

namespace Namotion.Interceptor.Generator;

internal static class SubjectPropertyConflicts
{
    /// <summary>
    /// Reports declarations that displace an ancestor subject's property, with or without 'new'.
    /// </summary>
    /// <remarks>
    /// Only ancestors marked [InterceptorSubject] are checked; hand-written subject bases are excluded.
    /// </remarks>
    public static HashSet<string>? ReportPropertiesDisplacingAnAncestorSubject(
        INamedTypeSymbol typeSymbol,
        IReadOnlyList<PropertyMetadata> classProperties,
        Location location,
        List<Diagnostic> diagnostics)
    {
        // Root subjects need no collection allocation during repeated IDE generation.
        List<INamedTypeSymbol>? subjectAncestors = null;
        foreach (var ancestor in SymbolExtensions.EnumerateChain(typeSymbol.BaseType))
        {
            if (SubjectAncestry.HasInterceptorSubjectAttribute(ancestor))
            {
                (subjectAncestors ??= new List<INamedTypeSymbol>()).Add(ancestor);
            }
        }

        if (subjectAncestors is null)
        {
            return null;
        }

        HashSet<string>? displacedNames = null;

        foreach (var property in classProperties)
        {
            // Overrides share the inherited slot. Explicit implementations can instead replace
            // an intercepted ancestor property with a non-intercepted interface read.
            if (property.IsOverride)
            {
                continue;
            }

            // Abstract properties also reach DefaultProperties; an intermediate class may implement them.
            var isDisplacing = subjectAncestors.Any(ancestor => ancestor
                .GetMembers(property.Name)
                .OfType<IPropertySymbol>()
                .Any(candidate => !SymbolExtensions.IsNeverASubjectProperty(candidate)));

            if (isDisplacing && (displacedNames ??= []).Add(property.Name))
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.DisplacesAncestorSubjectProperty, location,
                    typeSymbol.Name, property.Name));
            }
        }

        return displacedNames;
    }

    /// <summary>
    /// Reports properties shadowing an inherited interface implementation, allowing class and interface reads to diverge.
    /// </summary>
    public static void ReportPropertiesShadowingABaseImplementation(
        INamedTypeSymbol typeSymbol,
        IReadOnlyList<PropertyMetadata> classProperties,
        HashSet<string>? displacedNames,
        Location location,
        List<Diagnostic> diagnostics)
    {
        // Without a base class the subject's own declarations own every interface slot it has.
        if (typeSymbol.BaseType is not { } baseType || baseType.SpecialType == SpecialType.System_Object)
        {
            return;
        }

        foreach (var property in classProperties)
        {
            // Explicit implementations and overrides own the slot. Keep only NI0065 for displaced
            // properties: re-listing the interface would not fix their displacement.
            if (property.ExplicitInterfaceTypeName is not null ||
                property.IsOverride ||
                displacedNames?.Contains(property.Name) == true)
            {
                continue;
            }

            if (ShadowsBaseImplementation(typeSymbol, baseType, property.Name))
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.ShadowsBaseImplementation, location,
                    typeSymbol.Name, property.Name));
            }
        }
    }

    private static bool ShadowsBaseImplementation(INamedTypeSymbol typeSymbol, INamedTypeSymbol baseType, string propertyName)
    {
        foreach (var interfaceType in typeSymbol.AllInterfaces)
        {
            // A default implementation from an interface introduced by this subject is not
            // inherited from its base, even when a same-name property fails to implement it.
            if (!baseType.AllInterfaces.Contains(interfaceType, SymbolEqualityComparer.Default))
            {
                continue;
            }

            var interfaceMember = interfaceType
                .GetMembers(propertyName)
                .OfType<IPropertySymbol>()
                .FirstOrDefault();

            if (interfaceMember is null)
            {
                continue;
            }

            var implementation = typeSymbol.FindImplementationForInterfaceMember(interfaceMember);
            if (implementation is null ||
                SymbolEqualityComparer.Default.Equals(implementation.ContainingType, typeSymbol))
            {
                continue;
            }

            return true;
        }

        return false;
    }
}
