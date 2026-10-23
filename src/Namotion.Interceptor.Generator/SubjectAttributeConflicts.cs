using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Namotion.Interceptor.Generator;

internal static class SubjectAttributeConflicts
{
    /// <summary>
    /// Reports two properties of the subject's property set that attach the same attribute name to the same property.
    /// </summary>
    /// <remarks>
    /// Covers the subject's own properties and those of its [InterceptorSubject] ancestors, which is the set a
    /// generated subject exposes. An attribute that reaches a property only through an interface member, and a
    /// duplicate inside a hand-written subject base, are resolved by the registry at registration.
    /// </remarks>
    public static void ReportDuplicatePropertyAttributeTargets(
        INamedTypeSymbol typeSymbol,
        Location location,
        List<Diagnostic> diagnostics)
    {
        var subjectAncestors = SubjectAncestry.GetSubjectAncestors(typeSymbol);

        // Names are tracked only across a chain: within one class the compiler already keeps them unique.
        var seenNames = subjectAncestors is null ? null : new HashSet<string>();
        Dictionary<(string PropertyName, string AttributeName), (string ProviderName, bool IsOwn)>? providers = null;

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
        ref Dictionary<(string PropertyName, string AttributeName), (string ProviderName, bool IsOwn)>? providers,
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

            if (!TryGetPropertyAttributeTarget(property, out var target))
            {
                continue;
            }

            if (TryAddProvider(ref providers, target, property.Name, isOwn, out var provider))
            {
                continue;
            }

            // A duplicate that lives entirely in a base subject is reported there, not on every derived subject.
            if (isOwn || provider.IsOwn)
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
        ref Dictionary<(string PropertyName, string AttributeName), (string ProviderName, bool IsOwn)>? providers,
        (string PropertyName, string AttributeName) target,
        string propertyName,
        bool isOwn,
        out (string ProviderName, bool IsOwn) existingProvider)
    {
        providers ??= new Dictionary<(string PropertyName, string AttributeName), (string ProviderName, bool IsOwn)>();
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
    private static bool TryGetPropertyAttributeTarget(
        IPropertySymbol property,
        out (string PropertyName, string AttributeName) target)
    {
        for (var current = property; current is not null; current = current.OverriddenProperty)
        {
            if (TryGetDeclaredPropertyAttributeTarget(current, out target))
            {
                return true;
            }
        }

        target = default;
        return false;
    }

    private static bool TryGetDeclaredPropertyAttributeTarget(
        IPropertySymbol property,
        out (string PropertyName, string AttributeName) target)
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
