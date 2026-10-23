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
    /// duplicate inside a hand-written subject base, are left to the registry's check at registration.
    /// </remarks>
    public static void ReportDuplicatePropertyAttributeTargets(
        INamedTypeSymbol typeSymbol,
        Location location,
        List<Diagnostic> diagnostics)
    {
        var subjectAncestors = GetSubjectAncestors(typeSymbol);

        // Names are tracked only across a chain: within one class the compiler already keeps them unique.
        var seenNames = subjectAncestors.Count == 0 ? null : new HashSet<string>();
        var providers = new Dictionary<(string PropertyName, string AttributeName), (string ProviderName, bool IsOwn)>();

        CollectAndReport(typeSymbol, typeSymbol, true, seenNames, providers, location, diagnostics);
        foreach (var ancestor in subjectAncestors)
        {
            CollectAndReport(typeSymbol, ancestor, false, seenNames, providers, location, diagnostics);
        }
    }

    private static List<INamedTypeSymbol> GetSubjectAncestors(INamedTypeSymbol typeSymbol)
    {
        var subjectAncestors = new List<INamedTypeSymbol>();
        foreach (var ancestor in SymbolExtensions.EnumerateChain(typeSymbol.BaseType))
        {
            if (SubjectAncestry.HasInterceptorSubjectAttribute(ancestor))
            {
                subjectAncestors.Add(ancestor);
            }
        }

        return subjectAncestors;
    }

    private static void CollectAndReport(
        INamedTypeSymbol subject,
        INamedTypeSymbol type,
        bool isOwn,
        HashSet<string>? seenNames,
        Dictionary<(string PropertyName, string AttributeName), (string ProviderName, bool IsOwn)> providers,
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

            if (!providers.TryGetValue(target, out var provider))
            {
                providers[target] = (property.Name, isOwn);
            }
            else if (isOwn || provider.IsOwn)
            {
                // A duplicate that lives entirely in a base subject is reported there, not on every derived subject.
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.DuplicatePropertyAttributeTarget, location,
                    subject.Name, target.AttributeName, target.PropertyName, provider.ProviderName, property.Name));
            }
        }
    }

    // An explicit implementation's attributes are not part of the property metadata, and its name is qualified.
    private static bool TakesPartInUniqueness(IPropertySymbol property)
    {
        return !SymbolExtensions.IsNeverASubjectProperty(property) && property.ExplicitInterfaceImplementations.Length == 0;
    }

    private static bool TryGetPropertyAttributeTarget(
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
