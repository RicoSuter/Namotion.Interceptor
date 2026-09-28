using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Namotion.Interceptor.Generator;

/// <summary>
/// Finds inheritance conflicts with generated members and IInterceptorSubject implementations.
/// </summary>
internal static class SubjectMemberConflicts
{
    /// <summary>
    /// Returns root-mode member names requiring 'new' under C# hiding rules, including collisions across member kinds.
    /// </summary>
    public static IReadOnlyList<string> FindHiddenInterceptionMembers(
        INamedTypeSymbol? baseType,
        INamedTypeSymbol subject,
        Compilation compilation,
        bool emitsNotifyPropertyChanged)
    {
        if (baseType is null || baseType.SpecialType == SpecialType.System_Object)
        {
            return [];
        }

        var hidden = new List<string>();

        foreach (var accessorHelper in GeneratedMemberTable.AccessorHelpers)
        {
            var isHidden = SymbolExtensions.HidableMembers(baseType, subject, compilation, accessorHelper.Name)
                .Any(member => IsHiddenByEmittedMember(member, accessorHelper));

            if (isHidden)
            {
                hidden.Add(accessorHelper.Name);
            }
        }

        if (!emitsNotifyPropertyChanged)
        {
            return hidden;
        }

        // Events hide by name across member kinds; inaccessible explicit implementations do not count.
        if (SymbolExtensions.HidableMembers(baseType, subject, compilation, MemberNames.PropertyChanged).Any())
        {
            hidden.Add(MemberNames.PropertyChanged);
        }

        // An unrelated overload, such as RaisePropertyChanged(PropertyChangedEventArgs), does not
        // require 'new'; adding it would produce CS0109.
        var raiseIsHidden = SymbolExtensions.HidableMembers(baseType, subject, compilation, MemberNames.RaisePropertyChanged)
            .Any(member => member is not IMethodSymbol method || GeneratedMemberTable.HasRaisePropertyChangedParameters(method));

        if (raiseIsHidden)
        {
            hidden.Add(MemberNames.RaisePropertyChanged);
        }

        return hidden;
    }

    /// <summary>
    /// Checks helper method arity and parameter count; other member kinds hide by name.
    /// </summary>
    private static bool IsHiddenByEmittedMember(ISymbol member, AccessorHelperShape accessorHelper)
    {
        if (member is not IMethodSymbol method)
        {
            return true;
        }

        return method.TypeParameters.Length == accessorHelper.TypeParameterCount &&
               method.Parameters.Length == accessorHelper.ParameterCount;
    }

    /// <summary>
    /// Finds same-name members below the contract provider, including statics and overloads.
    /// Intermediate members must be accessible from the subject.
    /// </summary>
    public static IEnumerable<(INamedTypeSymbol Declarer, string MemberName)> FindHidingMembers(
        INamedTypeSymbol subject,
        INamedTypeSymbol contractProvider,
        Compilation compilation)
    {
        // Even a different-signature overload can capture calls intended for inherited generated members.
        foreach (var type in EnumerateBetween(subject, contractProvider))
        {
            foreach (var name in GeneratedMemberTable.GeneratedMemberNames)
            {
                if (HasHidingMember(type, name, subject, compilation))
                {
                    yield return (type, name);
                }
            }
        }
    }

    private static bool HasHidingMember(INamedTypeSymbol type, string name, INamedTypeSymbol subject, Compilation compilation)
    {
        foreach (var member in type.GetMembers(name))
        {
            if (SymbolEqualityComparer.Default.Equals(type, subject) || compilation.IsSymbolAccessibleWithin(member, subject))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Finds public or explicit implementations that take an IInterceptorSubject slot from an ancestor.
    /// </summary>
    public static IEnumerable<(INamedTypeSymbol Declarer, string MemberName)> FindHijackingMembers(
        INamedTypeSymbol subject,
        INamedTypeSymbol contractProvider,
        Compilation compilation)
    {
        var hijackableMembers = GeneratedMemberTable.GetHijackableInterfaceMembers(compilation);
        if (hijackableMembers.Length == 0)
        {
            yield break;
        }

        // The provider itself can reimplement an ancestor's slot, including through an explicit implementation.
        var isAtOrAboveContractProvider = false;

        foreach (var type in SymbolExtensions.EnumerateChain(subject))
        {
            isAtOrAboveContractProvider |= SymbolEqualityComparer.Default.Equals(type, contractProvider);

            foreach (var (name, interfaceMember) in hijackableMembers)
            {
                if (!DeclaresCandidateImplementation(type, name, interfaceMember))
                {
                    continue;
                }

                // Below the provider, inherited generated implementations may not yet be visible on symbols.
                if (isAtOrAboveContractProvider && !TakesSlotFromAbove(type, interfaceMember))
                {
                    continue;
                }

                yield return (type, name);
            }
        }
    }

    private static bool DeclaresCandidateImplementation(INamedTypeSymbol type, string name, ISymbol interfaceMember)
    {
        foreach (var member in type.GetMembers())
        {
            if (member.IsStatic || member.IsOverride)
            {
                continue;
            }

            var isPublicMatch = member.Name == name &&
                                member.DeclaredAccessibility == Accessibility.Public &&
                                IsImplicitImplementationOf(member, interfaceMember);
            if (isPublicMatch || IsExplicitInterceptorSubjectImplementation(member, name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether the declarer's base already implements the interface member.
    /// </summary>
    private static bool TakesSlotFromAbove(INamedTypeSymbol declarer, ISymbol interfaceMember)
        => declarer.BaseType?.FindImplementationForInterfaceMember(interfaceMember) is not null;

    /// <summary>
    /// Checks the type, signature, and accessors required for implicit interface implementation.
    /// </summary>
    private static bool IsImplicitImplementationOf(ISymbol member, ISymbol interfaceMember)
    {
        switch (interfaceMember)
        {
            case IPropertySymbol interfaceProperty:
                if (member is not IPropertySymbol property ||
                    !SymbolEqualityComparer.Default.Equals(property.Type, interfaceProperty.Type) ||
                    !ParametersMatch(property.Parameters, interfaceProperty.Parameters))
                {
                    return false;
                }

                return (interfaceProperty.GetMethod is null || IsPubliclyCallable(property.GetMethod)) &&
                       (interfaceProperty.SetMethod is null || IsPubliclyCallable(property.SetMethod));

            case IMethodSymbol interfaceMethod:
                return member is IMethodSymbol method &&
                       SymbolEqualityComparer.Default.Equals(method.ReturnType, interfaceMethod.ReturnType) &&
                       method.TypeParameters.Length == interfaceMethod.TypeParameters.Length &&
                       ParametersMatch(method.Parameters, interfaceMethod.Parameters);

            default:
                return false;
        }
    }

    /// <summary>
    /// Checks whether an accessor can implement a public interface accessor.
    /// </summary>
    private static bool IsPubliclyCallable(IMethodSymbol? accessor)
    {
        return accessor is { DeclaredAccessibility: Accessibility.Public };
    }

    /// <summary>
    /// Compares parameter types and ref kinds, excluding the non-signature 'params' modifier.
    /// </summary>
    private static bool ParametersMatch(
        ImmutableArray<IParameterSymbol> candidate,
        ImmutableArray<IParameterSymbol> required)
    {
        if (candidate.Length != required.Length)
        {
            return false;
        }

        for (var index = 0; index < candidate.Length; index++)
        {
            if (candidate[index].RefKind != required[index].RefKind ||
                !SymbolEqualityComparer.Default.Equals(candidate[index].Type, required[index].Type))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsExplicitInterceptorSubjectImplementation(ISymbol member, string name)
    {
        var explicitProperty = (member as IPropertySymbol)?.ExplicitInterfaceImplementations.FirstOrDefault();
        var explicitMethod = (member as IMethodSymbol)?.ExplicitInterfaceImplementations.FirstOrDefault();
        var implemented = (ISymbol?)explicitProperty ?? explicitMethod;

        return implemented is not null &&
               implemented.Name == name &&
               implemented.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == KnownTypes.IInterceptorSubject;
    }

    /// <summary>
    /// Enumerates the subject and its ancestors, excluding the contract provider and object.
    /// </summary>
    private static IEnumerable<INamedTypeSymbol> EnumerateBetween(INamedTypeSymbol subject, INamedTypeSymbol provider)
    {
        for (var current = subject;
             current is { SpecialType: not SpecialType.System_Object } &&
             !SymbolEqualityComparer.Default.Equals(current, provider);
             current = current.BaseType!)
        {
            yield return current;
        }
    }
}
