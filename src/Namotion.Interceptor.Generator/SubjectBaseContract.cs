using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Namotion.Interceptor.Generator.Models;

namespace Namotion.Interceptor.Generator;

/// <summary>
/// Everything the generator needs to know about the class a subject inherits from: which ancestor
/// owns the shared plumbing, and whether that ancestor exposes enough of it to be inherited from.
/// </summary>
internal static class SubjectBaseContract
{
    /// <summary>
    /// Resolves everything the emitter needs to know about the base class, and reports NI0011 to
    /// NI0014. A null result means generation is suppressed and the caller must emit nothing.
    /// </summary>
    public static BaseClassInfo? Resolve(
        INamedTypeSymbol typeSymbol,
        Compilation compilation,
        Location location,
        List<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        // Resolved from the symbol, not from the attributed declaration's base list: the base list
        // may sit on a partial declaration other than the attributed one, and the symbol's BaseType
        // chain is strictly base classes, so an interface in the base list is never mistaken for one.
        var subjectAncestor = SubjectAncestry.FindNearestSubjectAncestor(typeSymbol);

        var baseClassTypeName = subjectAncestor?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var baseClassHasInterceptorSubject = SubjectAncestry.HasInterceptorSubjectAttribute(subjectAncestor);

        var baseClassHasInpc = SubjectAncestry.InheritsNotifyPropertyChanged(typeSymbol, compilation, cancellationToken);
        var hasCallableRaisePropertyChanged = SubjectAncestry.HasCallableRaisePropertyChanged(typeSymbol, compilation, cancellationToken);

        // Root mode emits the whole IInterceptorSubject block; derived mode emits only its own
        // Properties line and inherits the rest.
        //
        // Mode selection, asked of the nearest subject ancestor and never of "some ancestor": a
        // hand-written IInterceptorSubject implementer between two generated subjects would
        // otherwise select derived mode and silently reproduce this bug, because Context resolves
        // to the middle's executor while the inherited helpers read the root's field.
        var emitsPlumbingHere = true;
        IReadOnlyList<string> hiddenPlumbingMembers = [];

        if (subjectAncestor is not null)
        {
            // An ancestor generated in this very compilation cannot be contract-checked: its
            // plumbing lives in source the generator has not emitted yet, so the symbol shows none
            // of it.
            var ancestorIsGeneratedHere =
                baseClassHasInterceptorSubject &&
                SubjectAncestry.WillBeGeneratedInThisCompilation(subjectAncestor, cancellationToken);

            if (ancestorIsGeneratedHere ||
                SatisfiesContract(subjectAncestor, typeSymbol, compilation, out var missingMembers))
            {
                emitsPlumbingHere = false;

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
                // Only emitsPlumbingHere flips. The ancestor stays the base-class fact source, so
                // DefaultProperties still concatenates with it and the INPC decision is unchanged.
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.BasePlumbingCannotBeShared,
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

        // Asked for every root-mode subject with a base class, not only for the NI0012 one: the
        // base does not have to be a subject at all for a collision to happen. An MVVM base
        // carrying PropertyChanged and RaisePropertyChanged is the common shape, and root mode
        // re-emits both, which is a CS0108 in a file the consumer cannot edit.
        //
        // The walk starts at the immediate base rather than at the subject ancestor, because
        // hiding is decided against the nearest declaration of the name anywhere above, including
        // a plain class sitting in between.
        //
        // A generated ancestor's plumbing does not exist as a symbol during this pass, so the
        // lookup cannot see it, but the generator knows it is about to emit it.
        if (emitsPlumbingHere)
        {
            hiddenPlumbingMembers = SubjectAncestry.HasGeneratedSubjectAncestor(typeSymbol, cancellationToken)
                ? GeneratedMemberTable.RootModePlumbingMemberNames
                : SubjectMemberConflicts.FindHiddenPlumbingMembers(typeSymbol.BaseType, typeSymbol, compilation, !baseClassHasInpc);
        }

        return new BaseClassInfo(
            baseClassTypeName,
            baseClassHasInterceptorSubject,
            baseClassHasInpc,
            hasCallableRaisePropertyChanged,
            emitsPlumbingHere,
            hiddenPlumbingMembers);
    }

    /// <summary>
    /// The members a class must expose to host a generated subclass. Generated root mode satisfies
    /// this by construction. Lookup walks the ancestor chain, so a member inherited by the ancestor
    /// from further up counts, and runs against the constructed type, so a generic base is checked
    /// with its type arguments substituted.
    /// </summary>
    /// <remarks>
    /// Every clause but one is required for the generated code to compile. The
    /// <see cref="KnownTypes.IRaisePropertyChanged"/> clause is deliberately stricter than that:
    /// when it is the only clause a shape fails, derived mode would still compile, because the
    /// subject declares its own PropertyChanged and RaisePropertyChanged whenever the base has
    /// neither. Failing the contract there costs that shape the plumbing and allocation sharing of
    /// derived mode, not the build.
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

        foreach (var plumbingMethod in GeneratedMemberTable.PlumbingMethods)
        {
            if (!HasAccessibleMethod(ancestor, subject, compilation, plumbingMethod))
            {
                missing.Add(plumbingMethod.Declaration);
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
    /// A static DefaultProperties that is both accessible and of a type the emitted .Concat(...)
    /// accepts. Checking only that some static of that name resolves lets a base declaring
    /// "public static int DefaultProperties" through, and the generated code then fails with
    /// CS1929, which is exactly the raw compiler error in generated code the diagnostics exist to
    /// replace. The type test compares against the constructed
    /// IReadOnlyDictionary&lt;string, SubjectPropertyMetadata&gt; rather than matching on the display
    /// string, which would accept an IReadOnlyList&lt;SubjectPropertyMetadata&gt; and produce that
    /// same CS1929. A field counts as well as a property: the emitted call site reads it the same
    /// way, so rejecting one would turn a shape that compiles today into an NI0011 error.
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
                var memberType = member switch
                {
                    IPropertySymbol property => property.Type,
                    IFieldSymbol field => field.Type,
                    _ => null
                };

                if (memberType is null ||
                    !member.IsStatic ||
                    !compilation.IsSymbolAccessibleWithin(member, subject))
                {
                    continue;
                }

                // The declared type itself, or any type that implements it: a Dictionary or a
                // FrozenDictionary converts to it implicitly and the emitted .Concat(...) consumes
                // it just as happily.
                if (SymbolEqualityComparer.Default.Equals(memberType, expectedType) ||
                    memberType.AllInterfaces.Contains(expectedType, SymbolEqualityComparer.Default))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// IReadOnlyDictionary&lt;string, SubjectPropertyMetadata&gt; as the compilation sees it, or null
    /// when either half is unreferenced, in which case nothing the generator emits would compile
    /// anyway.
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
        PlumbingMethodShape plumbingMethod)
        => SymbolExtensions.AccessibleMembers(ancestor, subject, compilation, plumbingMethod.Name)
            .OfType<IMethodSymbol>()
            .Any(method =>
                method.TypeParameters.Length == plumbingMethod.TypeParameterCount &&
                method.Parameters.Length == plumbingMethod.ParameterCount &&
                (!plumbingMethod.RequiresParameterArray ||
                 method.Parameters[method.Parameters.Length - 1].IsParams) &&
                (!plumbingMethod.RequiresLeadingString ||
                 method.Parameters[0].Type.SpecialType == SpecialType.System_String) &&
                HasExpectedReturnType(method, plumbingMethod, compilation));

    /// <summary>
    /// Whether the base helper returns what the generated call sites consume. Nullability
    /// annotations are deliberately not compared: the emitted code accepts both forms, and a base
    /// compiled without a nullable context would otherwise fail the contract for no reason.
    /// </summary>
    /// <remarks>
    /// The dictionary case accepts an implementing type as well as the declared interface, exactly
    /// like <see cref="HasUsableDefaultProperties"/>, because the two feed the same emitted
    /// expression, GetInstanceProperties() ?? DefaultProperties. Comparing by identity here rejects
    /// a base returning FrozenDictionary&lt;string, SubjectPropertyMetadata&gt;?, which the emitted
    /// expression consumes just as happily, and costs that base's own properties their interception.
    /// The reference type clause is the asymmetry with <see cref="HasUsableDefaultProperties"/>, and
    /// it is real rather than an oversight: this side is the left operand of '??', which rejects a
    /// value type with CS0019, while the other side feeds .Concat, where a struct is fine.
    /// </remarks>
    private static bool HasExpectedReturnType(
        IMethodSymbol method,
        PlumbingMethodShape plumbingMethod,
        Compilation compilation)
    {
        switch (plumbingMethod.ReturnKind)
        {
            case PlumbingReturnKind.OwnTypeParameter:
                return SymbolEqualityComparer.Default.Equals(method.ReturnType, method.TypeParameters[0]);

            case PlumbingReturnKind.Boolean:
                return method.ReturnType.SpecialType == SpecialType.System_Boolean;

            case PlumbingReturnKind.Object:
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
