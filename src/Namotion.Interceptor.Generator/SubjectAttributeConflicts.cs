using System.Collections.Generic;
using Microsoft.CodeAnalysis;

using AttributeTarget = (string PropertyName, string AttributeName);
using AttributeProvider = (string ProviderName, bool IsOwn);

namespace Namotion.Interceptor.Generator;

internal static class SubjectAttributeConflicts
{
    /// <summary>
    /// Reports two properties of the subject's property set that attach the same attribute name to the same property.
    /// </summary>
    /// <remarks>
    /// The shapes this does not see are listed with NI0066 in docs/generator.md; the registry resolves them at registration.
    /// </remarks>
    public static void ReportDuplicatePropertyAttributeTargets(
        INamedTypeSymbol typeSymbol,
        Location location,
        List<Diagnostic> diagnostics)
    {
        var subjectAncestors = SubjectAncestry.GetSubjectAncestors(typeSymbol);

        // Names are tracked only across a chain: within one class the compiler already keeps them unique.
        var seenNames = subjectAncestors is null ? null : new HashSet<string>();
        Dictionary<AttributeTarget, AttributeProvider>? providers = null;

        CollectAndReport(typeSymbol, typeSymbol, true, seenNames, ref providers, location, diagnostics);
        if (subjectAncestors is null)
        {
            return;
        }

        foreach (var ancestor in subjectAncestors)
        {
            CollectAndReport(typeSymbol, ancestor, false, seenNames, ref providers, location, diagnostics);
        }
    }

    private static void CollectAndReport(
        INamedTypeSymbol subject,
        INamedTypeSymbol type,
        bool isOwn,
        HashSet<string>? seenNames,
        ref Dictionary<AttributeTarget, AttributeProvider>? providers,
        Location location,
        List<Diagnostic> diagnostics)
    {
        foreach (var member in type.GetMembers())
        {
            if (member is not IPropertySymbol property || !TakesPartInUniqueness(property))
            {
                continue;
            }

            // An override or a displacing declaration shares the key of the ancestor property it replaces.
            if (seenNames is not null && !seenNames.Add(property.Name))
            {
                continue;
            }

            if (!TryGetPropertyAttributeTarget(property, isOwn, out var target, out var isOwnTarget))
            {
                continue;
            }

            if (TryAddProvider(ref providers, target, property.Name, isOwnTarget, out var provider))
            {
                continue;
            }

            // A duplicate that lives entirely in a base subject is reported there, not on every derived subject.
            if (isOwnTarget || provider.IsOwn)
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.DuplicatePropertyAttributeTarget, location,
                    subject.Name, target.AttributeName, target.PropertyName, provider.ProviderName, property.Name));
            }
        }
    }

    /// <summary>
    /// Records the property as the provider of the target, or returns false with the provider recorded before it.
    /// </summary>
    private static bool TryAddProvider(
        ref Dictionary<AttributeTarget, AttributeProvider>? providers,
        AttributeTarget target,
        string propertyName,
        bool isOwn,
        out AttributeProvider existingProvider)
    {
        providers ??= new Dictionary<AttributeTarget, AttributeProvider>();
        if (providers.TryGetValue(target, out existingProvider))
        {
            return false;
        }

        providers[target] = (propertyName, isOwn);
        return true;
    }

    // An explicit implementation's attributes are not part of the property metadata, and its name is qualified.
    private static bool TakesPartInUniqueness(IPropertySymbol property)
    {
        return !SymbolExtensions.IsNeverASubjectProperty(property) && property.ExplicitInterfaceImplementations.Length == 0;
    }

    // An override inherits the attribute of the declaration it overrides at runtime, but Roslyn reports only the
    // attributes declared on the override itself; the nearest declaration wins, matching AllowMultiple = false.
    // Once the walk passes a subject ancestor, that ancestor sees the attribute too and reports any duplicate itself,
    // so the target is not the subject's own. An attribute reached through plain classes only is.
    private static bool TryGetPropertyAttributeTarget(
        IPropertySymbol property,
        bool isOwn,
        out AttributeTarget target,
        out bool isOwnTarget)
    {
        var passedSubjectAncestor = false;
        for (var current = property; current is not null; current = current.OverriddenProperty)
        {
            passedSubjectAncestor |= !ReferenceEquals(current, property) &&
                SubjectAncestry.HasInterceptorSubjectAttribute(current.ContainingType);

            if (TryGetDeclaredPropertyAttributeTarget(current, out target))
            {
                isOwnTarget = isOwn && !passedSubjectAncestor;
                return true;
            }
        }

        target = default;
        isOwnTarget = false;
        return false;
    }

    private static bool TryGetDeclaredPropertyAttributeTarget(
        IPropertySymbol property,
        out AttributeTarget target)
    {
        foreach (var attribute in property.GetAttributes())
        {
            if (!SymbolExtensions.IsTypeOrInheritsFrom(attribute.AttributeClass, KnownTypes.PropertyAttributeAttribute))
            {
                continue;
            }

            if (attribute.ConstructorArguments.Length == 2 &&
                attribute.ConstructorArguments[0].Value is string propertyName &&
                attribute.ConstructorArguments[1].Value is string attributeName)
            {
                target = (propertyName, attributeName);
                return true;
            }
        }

        target = default;
        return false;
    }
}
