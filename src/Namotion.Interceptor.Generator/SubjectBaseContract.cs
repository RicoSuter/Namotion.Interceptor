using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Namotion.Interceptor.Generator.Models;

namespace Namotion.Interceptor.Generator;

/// <summary>
/// Resolves inherited interception members and validates the base contract.
/// </summary>
internal static class SubjectBaseContract
{
    /// <summary>
    /// Resolves the base contract and reports diagnostics. Returns null when the caller must suppress generation.
    /// </summary>
    public static SubjectBaseClass? Resolve(
        INamedTypeSymbol typeSymbol,
        Compilation compilation,
        Location location,
        List<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        // Symbols include base lists from other partial declarations and exclude interfaces from the base chain.
        var subjectAncestor = SubjectAncestry.FindNearestSubjectAncestor(typeSymbol);

        var baseClassTypeName = subjectAncestor?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var baseClassHasInterceptorSubject = SubjectAncestry.HasInterceptorSubjectAttribute(subjectAncestor);

        var baseClassHasInpc = SubjectAncestry.InheritsNotifyPropertyChanged(typeSymbol, compilation, cancellationToken);
        var hasCallableRaisePropertyChanged = SubjectAncestry.HasCallableRaisePropertyChanged(typeSymbol, compilation, cancellationToken);

        // Only the nearest subject ancestor can supply the shared interception members.
        var emitsInterceptionMembers = true;
        IReadOnlyList<string> hiddenMembers = [];

        if (subjectAncestor is not null)
        {
            // Members generated in this compilation are not yet visible on the ancestor's symbol.
            var ancestorIsGeneratedHere =
                baseClassHasInterceptorSubject &&
                SubjectAncestry.WillBeGeneratedInThisCompilation(subjectAncestor, cancellationToken);

            if (ancestorIsGeneratedHere ||
                SatisfiesContract(subjectAncestor, typeSymbol, compilation, out var missingMembers))
            {
                emitsInterceptionMembers = false;

                foreach (var (declarer, memberName) in SubjectMemberConflicts.FindHidingMembers(typeSymbol, subjectAncestor, compilation))
                {
                    diagnostics.Add(Diagnostic.Create(
                        Diagnostics.HidesGeneratedMember, location, declarer.ToDisplayString(), memberName));
                }

                foreach (var (declarer, memberName) in SubjectMemberConflicts.FindHijackingMembers(typeSymbol, subjectAncestor, compilation))
                {
                    diagnostics.Add(Diagnostic.Create(
                        Diagnostics.HijacksInterfaceImplementation, location, declarer.ToDisplayString(), memberName));
                }
            }
            else if (HasUsableDefaultProperties(subjectAncestor, typeSymbol, compilation))
            {
                // Root mode must retain the ancestor's DefaultProperties and notification behavior.
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.BaseInterceptionMembersCannotBeShared,
                    location,
                    subjectAncestor.ToDisplayString(),
                    typeSymbol.ToDisplayString(),
                    string.Join(", ", missingMembers)));
            }
            else
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.BaseDoesNotSatisfyContract,
                    location,
                    subjectAncestor.ToDisplayString(),
                    string.Join(", ", missingMembers)));

                return null;
            }
        }

        // Any base class can collide in root mode. Generated ancestor members need the table
        // because they are not yet visible on symbols.
        if (emitsInterceptionMembers)
        {
            hiddenMembers = SubjectAncestry.HasGeneratedSubjectAncestor(typeSymbol, cancellationToken)
                ? GeneratedMemberTable.RootModeMemberNames
                : SubjectMemberConflicts.FindHiddenInterceptionMembers(typeSymbol.BaseType, typeSymbol, compilation, !baseClassHasInpc);
        }

        return new SubjectBaseClass(
            baseClassTypeName,
            baseClassHasInterceptorSubject,
            baseClassHasInpc,
            hasCallableRaisePropertyChanged,
            emitsInterceptionMembers,
            hiddenMembers);
    }

    /// <summary>
    /// Checks the shared-member contract defined in docs/generator.md.
    /// </summary>
    /// <remarks>
    /// Missing <see cref="KnownTypes.IRaisePropertyChanged"/> prevents member sharing but can fall back to root mode.
    /// </remarks>
    private static bool SatisfiesContract(
        INamedTypeSymbol ancestor,
        INamedTypeSymbol subject,
        Compilation compilation,
        out IReadOnlyList<string> missingMembers)
    {
        var missing = new List<string>();

        if (!SymbolExtensions.ImplementsInterface(ancestor, KnownTypes.IInterceptorSubject))
        {
            missing.Add(KnownTypes.IInterceptorSubject);
        }

        if (!SymbolExtensions.ImplementsInterface(ancestor, KnownTypes.IRaisePropertyChanged) &&
            !SymbolExtensions.ImplementsInterface(subject, KnownTypes.IRaisePropertyChanged))
        {
            missing.Add(KnownTypes.IRaisePropertyChanged);
        }

        foreach (var accessorHelper in GeneratedMemberTable.AccessorHelpers)
        {
            if (!HasAccessibleMethod(ancestor, subject, compilation, accessorHelper))
            {
                missing.Add(accessorHelper.Declaration);
            }
        }

        if (!HasUsableDefaultProperties(ancestor, subject, compilation))
        {
            missing.Add("public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties");
        }

        missingMembers = missing;
        return missing.Count == 0;
    }

    /// <summary>
    /// Checks for an accessible static DefaultProperties field or property compatible with the emitted Concat call.
    /// </summary>
    private static bool HasUsableDefaultProperties(INamedTypeSymbol ancestor, INamedTypeSymbol subject, Compilation compilation)
    {
        var expectedType = GetPropertyMetadataDictionaryType(compilation);
        if (expectedType is null)
        {
            return false;
        }

        foreach (var candidate in SymbolExtensions.EnumerateChain(ancestor))
        {
            foreach (var member in candidate.GetMembers(MemberNames.DefaultProperties))
            {
                if (IsUsableDefaultPropertiesMember(member, subject, compilation, expectedType))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsUsableDefaultPropertiesMember(
        ISymbol member, INamedTypeSymbol subject, Compilation compilation, INamedTypeSymbol expectedType)
    {
        var memberType = member switch
        {
            IPropertySymbol property => property.Type,
            IFieldSymbol field => field.Type,
            _ => null
        };

        if (memberType is null || !member.IsStatic || !compilation.IsSymbolAccessibleWithin(member, subject))
        {
            return false;
        }

        return SymbolEqualityComparer.Default.Equals(memberType, expectedType) ||
               memberType.AllInterfaces.Contains(expectedType, SymbolEqualityComparer.Default);
    }

    /// <summary>
    /// Resolves IReadOnlyDictionary&lt;string, SubjectPropertyMetadata&gt;, or null when a required type is missing.
    /// </summary>
    private static INamedTypeSymbol? GetPropertyMetadataDictionaryType(Compilation compilation)
    {
        var dictionaryType = compilation.GetTypeByMetadataName("System.Collections.Generic.IReadOnlyDictionary`2");
        var propertyMetadataType = compilation.GetTypeByMetadataName(KnownTypes.SubjectPropertyMetadata);

        if (dictionaryType is null || propertyMetadataType is null)
        {
            return null;
        }

        return dictionaryType.Construct(compilation.GetSpecialType(SpecialType.System_String), propertyMetadataType);
    }

    private static bool HasAccessibleMethod(
        INamedTypeSymbol ancestor,
        INamedTypeSymbol subject,
        Compilation compilation,
        AccessorHelperShape accessorHelper)
        => SymbolExtensions.AccessibleMembers(ancestor, subject, compilation, accessorHelper.Name)
            .OfType<IMethodSymbol>()
            .Any(method => HasAccessibleMethodShape(method, accessorHelper, compilation));

    private static bool HasAccessibleMethodShape(
        IMethodSymbol method, AccessorHelperShape accessorHelper, Compilation compilation)
    {
        if (!accessorHelper.MatchesSignature(method))
        {
            return false;
        }

        if (accessorHelper.RequiresParameterArray && !method.Parameters[method.Parameters.Length - 1].IsParams)
        {
            return false;
        }

        return HasExpectedReturnType(method, accessorHelper, compilation);
    }

    /// <summary>
    /// Checks return-type compatibility with generated calls, ignoring nullability annotations.
    /// </summary>
    /// <remarks>
    /// Dictionary returns must be reference types for the emitted null-coalescing expression.
    /// </remarks>
    private static bool HasExpectedReturnType(
        IMethodSymbol method,
        AccessorHelperShape accessorHelper,
        Compilation compilation)
    {
        switch (accessorHelper.ReturnKind)
        {
            case AccessorHelperReturnKind.OwnTypeParameter:
                return SymbolEqualityComparer.Default.Equals(method.ReturnType, method.TypeParameters[0]);

            case AccessorHelperReturnKind.Boolean:
                return method.ReturnType.SpecialType == SpecialType.System_Boolean;

            case AccessorHelperReturnKind.Object:
                return method.ReturnType.SpecialType == SpecialType.System_Object;

            default:
                var expectedType = GetPropertyMetadataDictionaryType(compilation);
                return expectedType is not null &&
                       method.ReturnType.IsReferenceType &&
                       (SymbolEqualityComparer.Default.Equals(method.ReturnType, expectedType) ||
                        method.ReturnType.AllInterfaces.Contains(expectedType, SymbolEqualityComparer.Default));
        }
    }
}
