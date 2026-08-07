using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
        var subjectAncestor = FindNearestSubjectAncestor(typeSymbol);

        var baseClassTypeName = subjectAncestor?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var baseClassHasInterceptorSubject = HasInterceptorSubjectAttribute(subjectAncestor);

        var baseClassHasInpc = InheritsNotifyPropertyChanged(typeSymbol, compilation, cancellationToken);
        var hasCallableRaisePropertyChanged = HasCallableRaisePropertyChanged(typeSymbol, compilation, cancellationToken);

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
                WillBeGeneratedInThisCompilation(subjectAncestor, cancellationToken);

            if (ancestorIsGeneratedHere ||
                SatisfiesContract(subjectAncestor, typeSymbol, compilation, out var missingMembers))
            {
                emitsPlumbingHere = false;

                foreach (var (declarer, memberName) in FindHidingMembers(typeSymbol, subjectAncestor, compilation))
                {
                    diagnostics.Add(Diagnostic.Create(
                        Diagnostics.HidesGeneratedMember, location, declarer.ToDisplayString(), memberName));
                }

                foreach (var (declarer, memberName) in FindHijackingMembers(typeSymbol, subjectAncestor, compilation))
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
                    typeSymbol.ToDisplayString()));

                // A generated ancestor's plumbing does not exist as a symbol during this pass, so
                // the lookup below cannot see it, but the generator knows it is about to emit it.
                // Without this, every member root mode re-emits here hides the generated ancestor's
                // copy and produces a CS0108 in a file the consumer cannot edit.
                hiddenPlumbingMembers = HasGeneratedSubjectAncestor(typeSymbol, cancellationToken)
                    ? RootModePlumbingMemberNames
                    : FindHiddenPlumbingMembers(subjectAncestor, typeSymbol, compilation);
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

        return new BaseClassInfo(
            baseClassTypeName,
            baseClassHasInterceptorSubject,
            baseClassHasInpc,
            hasCallableRaisePropertyChanged,
            emitsPlumbingHere,
            hiddenPlumbingMembers);
    }

    /// <summary>
    /// Whether the class chain above the type already provides the INotifyPropertyChanged plumbing,
    /// so the subject must not declare its own.
    /// </summary>
    /// <remarks>
    /// The interface clause is deliberately asked of the TYPE and not of its subject ancestor: a
    /// base that implements IRaisePropertyChanged by hand without implementing IInterceptorSubject
    /// is not a subject ancestor at all, and dropping this would make its subclass re-declare
    /// PropertyChanged and RaisePropertyChanged. ManualInpcPersonBase in
    /// Namotion.Interceptor.Tracking.Tests is exactly that shape and has a live test.
    /// The attribute on its own is not evidence, only a promise: an attributed base can be declared
    /// without being partial, so nothing is ever generated into it, and a hand-written attributed
    /// base can carry no notify plumbing at all. Believing the promise leaves the subject with
    /// neither call form: the simple name is CS0103 and the interface cast throws at runtime.
    /// </remarks>
    private static bool InheritsNotifyPropertyChanged(
        INamedTypeSymbol typeSymbol,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        if (SymbolExtensions.ImplementsInterface(typeSymbol, KnownTypes.IRaisePropertyChanged))
        {
            return true;
        }

        var subjectAncestor = FindNearestSubjectAncestor(typeSymbol);
        if (subjectAncestor is null || !HasInterceptorSubjectAttribute(subjectAncestor))
        {
            return false;
        }

        // An ancestor generated in this compilation implements IRaisePropertyChanged in code that
        // does not exist as a symbol yet, and one built by an older generator may expose the raise
        // as a plain member without the interface.
        return WillBeGeneratedInThisCompilation(subjectAncestor, cancellationToken) ||
               HasCallableRaisePropertyChanged(typeSymbol, compilation, cancellationToken);
    }

    /// <summary>
    /// Whether a simple-name RaisePropertyChanged(name) call in the type's own body binds to a
    /// member above it. Emitting that form when it does not is CS0103 inside a generated file the
    /// consumer cannot edit, which is why the emitter falls back to the interface form.
    /// </summary>
    /// <remarks>
    /// The whole chain is walked, not just the nearest subject ancestor: a generated ancestor emits
    /// no raise of its own when its own base already provided the plumbing, so the member that
    /// answers the call can sit several classes further up. That is the shipped
    /// ManualInpcPersonBase shape. An explicit interface implementation is not found here and must
    /// not be: its name is qualified and it is private, so no simple-name call can reach it.
    /// </remarks>
    private static bool HasCallableRaisePropertyChanged(
        INamedTypeSymbol typeSymbol,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        if (typeSymbol.BaseType is not null &&
            AccessibleMembers(typeSymbol.BaseType, typeSymbol, compilation, MemberNames.RaisePropertyChanged)
                .OfType<IMethodSymbol>()
                .Any(IsRaisePropertyChangedSignature))
        {
            return true;
        }

        // An ancestor generated in this compilation has no member symbol yet. It emits one exactly
        // when nothing above it provides the plumbing, which is the same question asked here.
        return EnumerateChain(typeSymbol.BaseType).Any(ancestor =>
            HasInterceptorSubjectAttribute(ancestor) &&
            WillBeGeneratedInThisCompilation(ancestor, cancellationToken) &&
            !InheritsNotifyPropertyChanged(ancestor, compilation, cancellationToken));
    }

    private static bool IsRaisePropertyChangedSignature(IMethodSymbol method)
        => method.TypeParameters.Length == 0 &&
           method.Parameters.Length == 1 &&
           method.Parameters[0].RefKind == RefKind.None &&
           method.Parameters[0].Type.SpecialType == SpecialType.System_String;

    /// <summary>
    /// Whether any ancestor, not only the nearest subject one, is an in-source subject that will
    /// actually receive generated plumbing. Asked of the whole chain because a hand-written class in
    /// between is exactly what pushes this subject back into root mode, and the generated ancestor
    /// above it still owns the members this one is about to re-emit.
    /// </summary>
    private static bool HasGeneratedSubjectAncestor(INamedTypeSymbol typeSymbol, CancellationToken cancellationToken)
        => EnumerateChain(typeSymbol.BaseType)
            .Any(ancestor => HasInterceptorSubjectAttribute(ancestor) &&
                             WillBeGeneratedInThisCompilation(ancestor, cancellationToken));

    /// <summary>
    /// Whether an attributed ancestor declared in this compilation will actually receive generated
    /// plumbing. Carrying the attribute is not enough: NI0001 suppresses generation for a subject
    /// that is not partial, and assuming the plumbing appears anyway puts the subclass into derived
    /// mode, replacing one actionable diagnostic on the base with a wall of raw errors in a
    /// generated file the user cannot edit.
    /// </summary>
    private static bool WillBeGeneratedInThisCompilation(INamedTypeSymbol ancestor, CancellationToken cancellationToken)
    {
        if (ancestor.DeclaringSyntaxReferences.Length == 0)
        {
            return false;
        }

        foreach (var syntaxReference in ancestor.DeclaringSyntaxReferences)
        {
            if (syntaxReference.GetSyntax(cancellationToken) is not ClassDeclarationSyntax declaration ||
                !declaration.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The first ancestor that is a subject, skipping ordinary classes in between. Plain classes
    /// between two subjects are common enough to matter and reading the immediate base instead
    /// makes the generator emit a second copy of everything it already inherited.
    /// </summary>
    private static INamedTypeSymbol? FindNearestSubjectAncestor(INamedTypeSymbol typeSymbol)
        => EnumerateChain(typeSymbol.BaseType)
            .FirstOrDefault(ancestor =>
                HasInterceptorSubjectAttribute(ancestor) || DeclaresInterceptorSubject(ancestor));

    private static bool HasInterceptorSubjectAttribute(INamedTypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }

        return type
            .GetAttributes()
            .Any(a => SymbolExtensions.IsTypeOrInheritsFrom(a.AttributeClass, KnownTypes.InterceptorSubjectAttribute));
    }

    /// <summary>
    /// Whether the type itself declares IInterceptorSubject, directly or through an interface it
    /// declares. Deliberately not AllInterfaces and deliberately no BaseType recursion: those
    /// report interfaces inherited from a base class, which would stop the ancestor walk at a
    /// plain intermediate whenever the real subject ancestor comes from a metadata reference,
    /// that is, in every cross-assembly hierarchy.
    /// </summary>
    private static bool DeclaresInterceptorSubject(INamedTypeSymbol type)
    {
        return type.Interfaces.Any(declared =>
            IsInterceptorSubject(declared) || declared.AllInterfaces.Any(IsInterceptorSubject));
    }

    private static bool IsInterceptorSubject(INamedTypeSymbol interfaceType)
    {
        return interfaceType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == KnownTypes.IInterceptorSubject;
    }

    /// <summary>
    /// The names of the members root mode emits, read both by the lookups in this file and by the
    /// emitter when it decides which of them needs a 'new' modifier. A name that drifts between the
    /// two fails silently: the lookups would answer for a member the emitter never writes, and the
    /// emitted member would lose the modifier that keeps CS0108 out of a file the consumer cannot
    /// edit. The emitter writes the signatures themselves, whose spelling the compiler checks.
    /// </summary>
    public static class MemberNames
    {
        public const string GetPropertyValue = "GetPropertyValue";
        public const string SetPropertyValue = "SetPropertyValue";
        public const string InvokeMethod = "InvokeMethod";
        public const string GetInstanceProperties = "GetInstanceProperties";
        public const string PropertyChanged = "PropertyChanged";
        public const string RaisePropertyChanged = "RaisePropertyChanged";
    }

    /// <summary>
    /// The shape of one helper method root mode emits, in the single place both the contract check
    /// and the hiding check read it from. Parameter types are approximated by counts, which is
    /// enough to separate the emitted signature from an unrelated overload of the same name.
    /// </summary>
    /// <param name="Name">The emitted member name, from <see cref="MemberNames"/>.</param>
    /// <param name="TypeParameterCount">Type parameters on the emitted signature.</param>
    /// <param name="ParameterCount">Value parameters on the emitted signature.</param>
    /// <param name="RequiresParameterArray">
    /// Consulted by the contract check only. The emitted call site uses expanded form,
    /// InvokeMethod("M", lambda, p1), so a base declaring the same parameter types without params
    /// would pass a signature match and then fail at the call. Hiding is the opposite: params is not
    /// part of the signature C# hides by, so such a base does hide the emitted method and the 'new'
    /// modifier is still required.
    /// </param>
    /// <param name="Declaration">How the member is named in the NI0011 message.</param>
    private sealed record PlumbingMethodShape(
        string Name,
        int TypeParameterCount,
        int ParameterCount,
        bool RequiresParameterArray,
        string Declaration);

    private static readonly PlumbingMethodShape[] PlumbingMethods =
    [
        new PlumbingMethodShape(
            MemberNames.GetPropertyValue, TypeParameterCount: 1, ParameterCount: 2, RequiresParameterArray: false,
            "protected TProperty GetPropertyValue<TProperty>(string, Func<IInterceptorSubject, TProperty>)"),
        new PlumbingMethodShape(
            MemberNames.SetPropertyValue, TypeParameterCount: 1, ParameterCount: 4, RequiresParameterArray: false,
            "protected bool SetPropertyValue<TProperty>(string, TProperty, TProperty, Action<IInterceptorSubject, TProperty>)"),
        new PlumbingMethodShape(
            MemberNames.InvokeMethod, TypeParameterCount: 0, ParameterCount: 3, RequiresParameterArray: true,
            "protected object? InvokeMethod(string, Func<IInterceptorSubject, object?[], object?>, params object?[])"),
        new PlumbingMethodShape(
            MemberNames.GetInstanceProperties, TypeParameterCount: 0, ParameterCount: 0, RequiresParameterArray: false,
            "protected IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties()")
    ];

    /// <summary>
    /// Derived from <see cref="PlumbingMethods"/> rather than repeated, so a fifth helper added
    /// there cannot be contract-checked and silently escape the hiding rule.
    /// </summary>
    /// <remarks>
    /// This and <see cref="RootModePlumbingMemberNames"/> read the table declared above them, and a
    /// static field initializer runs in textual order, so they are kept adjacent to it: moved apart
    /// and reordered, they would initialize to empty or null and take the generator down with a
    /// TypeInitializationException on every compilation.
    /// </remarks>
    private static readonly string[] GeneratedMemberNames =
        PlumbingMethods.Select(shape => shape.Name).ToArray();

    /// <summary>
    /// Every member root mode emits that a generated copy further up the chain would hide. The two
    /// INPC members are part of it although nothing else in this file reads them: they are emitted
    /// by the same root-mode block and hide the ancestor's copies exactly like the helpers do.
    /// </summary>
    private static readonly string[] RootModePlumbingMemberNames =
        GeneratedMemberNames.Concat([MemberNames.PropertyChanged, MemberNames.RaisePropertyChanged]).ToArray();

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

        foreach (var plumbingMethod in PlumbingMethods)
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

        foreach (var candidate in EnumerateChain(ancestor))
        {
            foreach (var member in candidate.GetMembers("DefaultProperties"))
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

    /// <summary>
    /// The root-mode member names that need a 'new' modifier because the ancestor chain already
    /// exposes an accessible member the emitted one hides. This is C#'s hiding rule, not the
    /// contract's match: CS0108 fires for a same-name member of a DIFFERENT kind too, while a
    /// blanket 'new' produces CS0109 when nothing is hidden. Both are build errors under
    /// TreatWarningsAsErrors, so the modifier has to be decided per member.
    /// </summary>
    private static IReadOnlyList<string> FindHiddenPlumbingMembers(
        INamedTypeSymbol? ancestor,
        INamedTypeSymbol subject,
        Compilation compilation)
    {
        if (ancestor is null)
        {
            return [];
        }

        var hidden = new List<string>();

        foreach (var plumbingMethod in PlumbingMethods)
        {
            var isHidden = AccessibleMembers(ancestor, subject, compilation, plumbingMethod.Name)
                .Any(member => IsHiddenByEmittedMember(member, plumbingMethod));

            if (isHidden)
            {
                hidden.Add(plumbingMethod.Name);
            }
        }

        return hidden;
    }

    /// <summary>
    /// A method is hidden only when its signature matches the emitted one, so an unrelated overload
    /// of a plumbing name hides nothing and must not attract a 'new'. Everything else hides by name
    /// alone: a base property or field named GetPropertyValue is hidden by the emitted method.
    /// </summary>
    private static bool IsHiddenByEmittedMember(ISymbol member, PlumbingMethodShape plumbingMethod)
    {
        if (member is not IMethodSymbol method)
        {
            return true;
        }

        return method.TypeParameters.Length == plumbingMethod.TypeParameterCount &&
               method.Parameters.Length == plumbingMethod.ParameterCount;
    }

    private static bool HasAccessibleMethod(
        INamedTypeSymbol ancestor,
        INamedTypeSymbol subject,
        Compilation compilation,
        PlumbingMethodShape plumbingMethod)
        => AccessibleMembers(ancestor, subject, compilation, plumbingMethod.Name)
            .OfType<IMethodSymbol>()
            .Any(method =>
                method.TypeParameters.Length == plumbingMethod.TypeParameterCount &&
                method.Parameters.Length == plumbingMethod.ParameterCount &&
                (!plumbingMethod.RequiresParameterArray ||
                 method.Parameters[method.Parameters.Length - 1].IsParams));

    /// <summary>
    /// The members of a given name that member lookup from the subject would find on the ancestor
    /// chain. Statics are dropped because none of the emitted call sites can reach one, and
    /// inaccessible members because they neither hide nor bind. Callers add the part that actually
    /// differs between them: the contract check tests the signature, the hiding check tests C#'s
    /// hiding rule, and <see cref="HasCallableRaisePropertyChanged"/> tests the emitted call's one
    /// argument. Deliberately not used by <see cref="FindHidingMembers"/>, which must see statics.
    /// </summary>
    private static IEnumerable<ISymbol> AccessibleMembers(
        INamedTypeSymbol ancestor,
        INamedTypeSymbol subject,
        Compilation compilation,
        string name)
        => EnumerateChain(ancestor)
            .SelectMany(type => type.GetMembers(name))
            .Where(member => !member.IsStatic && compilation.IsSymbolAccessibleWithin(member, subject));

    private static IEnumerable<INamedTypeSymbol> EnumerateChain(INamedTypeSymbol? type)
    {
        for (var current = type; current is { SpecialType: not SpecialType.System_Object }; current = current.BaseType)
        {
            yield return current;
        }
    }

    private static readonly string[] HijackableInterfaceMembers =
        ["Context", "Data", "SyncRoot", "AddProperties"];

    /// <summary>
    /// Members named like an inherited generated member. Deliberately name-only, any kind, no
    /// signature test: a 'new' annotated member of the same shape captures the generated call with
    /// no compiler diagnostic at all, and an applicable overload with a different signature can win
    /// overload resolution without hiding anything. Reporting the name covers both. Statics are
    /// included, because C# hiding is not staticness-sensitive and a static called by simple name
    /// from an instance body captures the generated call just as quietly. On intermediate classes
    /// the scan is restricted to members accessible from the subject, because a private member
    /// neither hides nor is found by member lookup.
    /// </summary>
    private static IEnumerable<(INamedTypeSymbol Declarer, string MemberName)> FindHidingMembers(
        INamedTypeSymbol subject,
        INamedTypeSymbol contractProvider,
        Compilation compilation)
    {
        foreach (var type in EnumerateBetween(subject, contractProvider))
        {
            foreach (var name in GeneratedMemberNames)
            {
                foreach (var member in type.GetMembers(name))
                {
                    if (!SymbolEqualityComparer.Default.Equals(type, subject) &&
                        !compilation.IsSymbolAccessibleWithin(member, subject))
                    {
                        continue;
                    }

                    yield return (type, name);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Public members, and explicit interface implementations, that would take an IInterceptorSubject
    /// slot from the root under interface re-implementation. Context is the severe one: hijacking it
    /// leaves the inherited helpers reading a context that is never populated, so interception stops
    /// silently and the unguarded IInterceptorExecutor casts in DynamicSubjectFactory and
    /// RegisteredSubject throw.
    /// </summary>
    /// <remarks>
    /// The explicit form is reported on the subject itself too. The subject's own generated half is
    /// the only thing this generator authored, it contains nothing but
    /// IInterceptorSubject.Properties in derived mode, and a hand-written explicit Context on the
    /// user's half is exactly the severe case: it compiles with no diagnostic and kills interception
    /// entirely, writes landing in the backing fields so the values still look right.
    /// </remarks>
    private static IEnumerable<(INamedTypeSymbol Declarer, string MemberName)> FindHijackingMembers(
        INamedTypeSymbol subject,
        INamedTypeSymbol contractProvider,
        Compilation compilation)
    {
        var interfaceType = compilation.GetTypeByMetadataName(KnownTypes.IInterceptorSubject);
        if (interfaceType is null)
        {
            yield break;
        }

        var hijackableMembers = HijackableInterfaceMembers
            .Select(name => (Name: name, Member: interfaceType.GetMembers(name).FirstOrDefault()))
            .Where(entry => entry.Member is not null)
            .ToArray();

        foreach (var type in EnumerateBetween(subject, contractProvider))
        {
            foreach (var (name, interfaceMember) in hijackableMembers)
            {
                foreach (var member in type.GetMembers())
                {
                    if (member.IsStatic)
                    {
                        continue;
                    }

                    var isPublicMatch = member.Name == name &&
                                        member.DeclaredAccessibility == Accessibility.Public &&
                                        IsImplicitImplementationOf(member, interfaceMember!);

                    var isExplicitMatch = IsExplicitInterceptorSubjectImplementation(member, name);

                    if (isPublicMatch || isExplicitMatch)
                    {
                        yield return (type, name);
                        break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Whether a public member really is an implicit implementation of the interface member, which
    /// is what taking the slot requires. Name alone is not enough and reporting on it is a hard
    /// break: a partial string Data, a get-only object Data and a bool-returning AddProperties all
    /// compile, keep the root's implementations, and are perfectly ordinary names on a domain model.
    /// </summary>
    private static bool IsImplicitImplementationOf(ISymbol member, ISymbol interfaceMember)
    {
        switch (interfaceMember)
        {
            case IPropertySymbol interfaceProperty:
                return member is IPropertySymbol property &&
                       SymbolEqualityComparer.Default.Equals(property.Type, interfaceProperty.Type) &&
                       ParametersMatch(property.Parameters, interfaceProperty.Parameters) &&
                       (interfaceProperty.GetMethod is null || IsPubliclyCallable(property.GetMethod)) &&
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
    /// An accessor only implements an interface accessor when it is itself public: a
    /// "public string P { get; private set; }" does not implement a settable interface property.
    /// </summary>
    private static bool IsPubliclyCallable(IMethodSymbol? accessor)
    {
        return accessor is { DeclaredAccessibility: Accessibility.Public };
    }

    /// <summary>
    /// The 'params' modifier is deliberately not compared: it is not part of the signature, so a
    /// plain IEnumerable parameter still implements a params one.
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
    /// The subject and every class between it and the class providing the contract member, which
    /// is where a capturing or hijacking member can sit. Members in the provider itself are
    /// excluded: interface mapping prefers a class's own explicit implementation over its own
    /// public members.
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
