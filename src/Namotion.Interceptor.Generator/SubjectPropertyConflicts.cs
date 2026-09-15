using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Namotion.Interceptor.Generator.Models;

namespace Namotion.Interceptor.Generator;

internal static class SubjectPropertyConflicts
{
    /// <summary>
    /// A declaration in the subject's own class that displaces a property an ancestor subject already
    /// contributes to its DefaultProperties. Reported by effect rather than by the 'new' keyword,
    /// because omitting the keyword is only CS0108 and the displacement is identical either way.
    /// </summary>
    /// <remarks>
    /// Only ancestors carrying [InterceptorSubject] are scanned, so a declaration displacing a property
    /// of a hand-written base that satisfies the subject base contract is not reported.
    /// </remarks>
    public static HashSet<string>? ReportPropertiesDisplacingAnAncestorSubject(
        INamedTypeSymbol typeSymbol,
        IReadOnlyList<PropertyMetadata> classProperties,
        Location location,
        List<Diagnostic> diagnostics)
    {
        // Allocate nothing before the early return: every root subject reaches this and leaves through
        // it, and the IDE re-runs the generator on each keystroke.
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
            // An override shares the slot it already had, so one accessor pair stays reachable, only
            // one of the two backing fields is ever used, and it displaces nothing. An explicit
            // implementation is deliberately NOT skipped here: it lands in the highest precedence tier
            // and would flip an ancestor's intercepted property to a non-intercepted interface read.
            if (property.IsOverride)
            {
                continue;
            }

            // Abstract ancestor properties are deliberately NOT filtered out. They reach
            // DefaultProperties like any other, and a plain class between the two subjects can supply
            // the override that makes a 'new' declaration below it both legal and silent.
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
    /// Reports a class-declared property whose name matches an interface member that resolves to an
    /// implementation outside this type, so that reading through the interface and reading through
    /// the subject return different values.
    /// </summary>
    /// <remarks>
    /// Interface implementation is fixed where the interface joins the base list, so a property
    /// declared further down the hierarchy does not take over the slot. This must not fire on the
    /// ordinary shape, where the subject itself declares support for the interface and its own
    /// property is the implementation, nor on an override, which shares the base member's slot.
    /// </remarks>
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
            // An explicit implementation is by definition the implementation, and an override
            // shares the slot of the base member it overrides. A name NI0065 already reported keeps
            // only that report: both rules can match one declaration, and re-listing the interface,
            // this rule's remedy, would leave the displacement in place.
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
            // The message claims the base class already implements this member, so that must
            // be checked directly: an interface the subject itself lists (not one the base type
            // carries) is the subject's own to implement, even when its own same-named property
            // fails to bind to it (a type or accessor mismatch, say) and the interface's default
            // body ends up as the resolved implementation instead. That default is not the base
            // class's doing, so it must not be blamed as one.
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
