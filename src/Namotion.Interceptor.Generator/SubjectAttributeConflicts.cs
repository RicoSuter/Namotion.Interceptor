using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using AttributeTarget = (string PropertyName, string AttributeName);

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
        Compilation compilation,
        Location location,
        List<Diagnostic> diagnostics)
    {
        var subjectAncestors = SubjectAncestry.GetSubjectAncestors(typeSymbol);
        var providers = BuildProviderMap(typeSymbol, subjectAncestors, 0, compilation);
        if (providers is null || !HasDuplicate(providers))
        {
            return;
        }

        var ancestorProviders = subjectAncestors is null ? null : BuildProviderMap(subjectAncestors[0], subjectAncestors, 1, compilation);
        foreach (var entry in providers)
        {
            if (entry.Value.Count > 1)
            {
                ReportAddedProviders(typeSymbol, entry.Key, entry.Value, GetProviders(ancestorProviders, entry.Key), location, diagnostics);
            }
        }
    }

    /// <summary>
    /// Groups the names of the subject's property set by the attribute target each carries at runtime, or returns
    /// null when none carries one. The property set is <paramref name="subject"/>'s own properties followed by the
    /// entries of <paramref name="subjectAncestors"/> from <paramref name="firstAncestorIndex"/> on, which are the
    /// ancestors of <paramref name="subject"/> (index 1 when <paramref name="subject"/> is the list's first entry).
    /// </summary>
    private static Dictionary<AttributeTarget, List<string>>? BuildProviderMap(
        INamedTypeSymbol subject,
        List<INamedTypeSymbol>? subjectAncestors,
        int firstAncestorIndex,
        Compilation compilation)
    {
        var ancestorCount = subjectAncestors?.Count ?? 0;

        // Names are tracked only across a chain: within one class the compiler already keeps them unique.
        var seenNames = firstAncestorIndex < ancestorCount ? new HashSet<string>() : null;
        Dictionary<AttributeTarget, List<string>>? providers = null;

        AddProviders(subject, seenNames, ref providers, compilation);
        for (var index = firstAncestorIndex; index < ancestorCount; index++)
        {
            AddProviders(subjectAncestors![index], seenNames, ref providers, compilation);
        }

        return providers;
    }

    private static void AddProviders(
        INamedTypeSymbol type,
        HashSet<string>? seenNames,
        ref Dictionary<AttributeTarget, List<string>>? providers,
        Compilation compilation)
    {
        foreach (var member in type.GetMembers())
        {
            if (member is not IPropertySymbol property || !IsClassDeclaredSubjectProperty(property))
            {
                continue;
            }

            // An override or a displacing declaration shares the key of the ancestor property it replaces.
            if (seenNames is not null && !seenNames.Add(property.Name))
            {
                continue;
            }

            if (TryGetRuntimePropertyAttributeTarget(property, compilation, out var target))
            {
                AddProvider(ref providers, target, property.Name);
            }
        }

        if (seenNames is not null)
        {
            ClaimExplicitImplementationNames(type, seenNames);
        }
    }

    // An explicit implementation is keyed by its simple name and displaces an ancestor property of that name, but its
    // attributes are not part of the metadata, so it claims the name without providing a target. A class-declared
    // property of the same name keeps it, which is why the class's own properties are added first.
    private static void ClaimExplicitImplementationNames(INamedTypeSymbol type, HashSet<string> seenNames)
    {
        foreach (var member in type.GetMembers())
        {
            if (member is IPropertySymbol property &&
                !SymbolExtensions.IsNeverASubjectProperty(property) &&
                property.ExplicitInterfaceImplementations.Length > 0)
            {
                seenNames.Add(property.ExplicitInterfaceImplementations[0].Name);
            }
        }
    }

    private static void AddProvider(
        ref Dictionary<AttributeTarget, List<string>>? providers,
        AttributeTarget target,
        string propertyName)
    {
        providers ??= new Dictionary<AttributeTarget, List<string>>();
        if (!providers.TryGetValue(target, out var names))
        {
            names = new List<string>(1);
            providers[target] = names;
        }

        names.Add(propertyName);
    }

    private static bool HasDuplicate(Dictionary<AttributeTarget, List<string>> providers)
    {
        foreach (var names in providers.Values)
        {
            if (names.Count > 1)
            {
                return true;
            }
        }

        return false;
    }

    private static List<string>? GetProviders(Dictionary<AttributeTarget, List<string>>? providers, AttributeTarget target)
    {
        return providers is not null && providers.TryGetValue(target, out var names) ? names : null;
    }

    // A provider the nearest subject ancestor already has is reported there, so only the providers this subject adds
    // are reported, each against one the ancestor has (or the first one when the ancestor has none of them).
    private static void ReportAddedProviders(
        INamedTypeSymbol subject,
        AttributeTarget target,
        List<string> names,
        List<string>? ancestorNames,
        Location location,
        List<Diagnostic> diagnostics)
    {
        var anchor = FindAnchor(names, ancestorNames);
        foreach (var name in names)
        {
            if (name != anchor && ancestorNames?.Contains(name) != true)
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.DuplicatePropertyAttributeTarget, location,
                    subject.Name, target.AttributeName, target.PropertyName, anchor, name));
            }
        }
    }

    private static string FindAnchor(List<string> names, List<string>? ancestorNames)
    {
        if (ancestorNames is not null)
        {
            foreach (var name in names)
            {
                if (ancestorNames.Contains(name))
                {
                    return name;
                }
            }
        }

        return names[0];
    }

    private static bool IsClassDeclaredSubjectProperty(IPropertySymbol property)
    {
        return !SymbolExtensions.IsNeverASubjectProperty(property) && property.ExplicitInterfaceImplementations.Length == 0;
    }

    // The nearest declaration wins, matching AllowMultiple = false. Roslyn reports only the attributes declared on a
    // property itself, so the walk adds what the runtime inherits.
    private static bool TryGetRuntimePropertyAttributeTarget(
        IPropertySymbol property, Compilation compilation, out AttributeTarget target)
    {
        for (var current = property; current is not null; current = GetParentDeclaration(current, compilation))
        {
            if (TryGetDeclaredPropertyAttributeTarget(current, out target))
            {
                return true;
            }
        }

        target = default;
        return false;
    }

    // The runtime finds the parent through the declaration the getter overrides, or the setter when no getter is
    // declared, so an override of one accessor can skip a class that overrides only the other. It then looks the
    // parent up by property type, so a covariant-return override inherits nothing.
    private static IPropertySymbol? GetParentDeclaration(IPropertySymbol property, Compilation compilation)
    {
        var parent = (property.GetMethod ?? property.SetMethod)?.OverriddenMethod?.AssociatedSymbol as IPropertySymbol;
        return parent is not null && HaveSameRuntimeType(parent.Type, property.Type, compilation) ? parent : null;
    }

    // An identity conversion is exactly "the same type at runtime": it ignores dynamic versus object, tuple element
    // names and nullable annotations at any depth, as the runtime's lookup by property type does.
    private static bool HaveSameRuntimeType(ITypeSymbol left, ITypeSymbol right, Compilation compilation)
    {
        return compilation is CSharpCompilation csharpCompilation
            ? csharpCompilation.ClassifyConversion(left, right).IsIdentity
            : SymbolEqualityComparer.Default.Equals(left, right);
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
