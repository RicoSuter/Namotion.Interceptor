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

    // The target counts as the subject's own unless the nearest subject ancestor on the override path already sees
    // the same target for this property: that ancestor then reports the duplicate itself.
    private static bool TryGetPropertyAttributeTarget(
        IPropertySymbol property,
        bool isOwn,
        out AttributeTarget target,
        out bool isOwnTarget)
    {
        if (!TryGetInheritedPropertyAttributeTarget(property, out target))
        {
            isOwnTarget = false;
            return false;
        }

        isOwnTarget = isOwn && !IsSeenBySubjectAncestor(property, target);
        return true;
    }

    private static bool IsSeenBySubjectAncestor(IPropertySymbol property, AttributeTarget target)
    {
        for (var current = GetOverriddenDeclaration(property); current is not null; current = GetOverriddenDeclaration(current))
        {
            if (SubjectAncestry.HasInterceptorSubjectAttribute(current.ContainingType))
            {
                return TryGetInheritedPropertyAttributeTarget(current, out var ancestorTarget) && ancestorTarget.Equals(target);
            }
        }

        return false;
    }

    // The nearest declaration wins, matching AllowMultiple = false. Roslyn reports only the attributes declared on a
    // property itself, so the walk adds what the runtime inherits.
    private static bool TryGetInheritedPropertyAttributeTarget(IPropertySymbol property, out AttributeTarget target)
    {
        for (var current = property; current is not null; current = GetOverriddenDeclaration(current))
        {
            if (TryGetDeclaredPropertyAttributeTarget(current, out target))
            {
                return true;
            }
        }

        target = default;
        return false;
    }

    // The runtime inherits a property's attributes through the declaration its getter overrides, or its setter when
    // it declares no getter, so an override of one accessor can skip a class that overrides only the other.
    private static IPropertySymbol? GetOverriddenDeclaration(IPropertySymbol property)
    {
        return (property.GetMethod ?? property.SetMethod)?.OverriddenMethod?.AssociatedSymbol as IPropertySymbol;
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
