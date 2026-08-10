using System.Linq;
using Microsoft.CodeAnalysis;

namespace Namotion.Interceptor.Generator;

/// <summary>
/// The names of the members root mode emits, read both by the lookups in this assembly and by the
/// emitter when it decides which of them needs a 'new' modifier. A name that drifts between the two
/// fails silently: the lookups would answer for a member the emitter never writes, and the emitted
/// member would lose the modifier that keeps CS0108 out of a file the consumer cannot edit. The
/// emitter writes the signatures themselves, whose spelling the compiler checks.
/// </summary>
internal static class MemberNames
{
    public const string GetPropertyValue = "GetPropertyValue";
    public const string SetPropertyValue = "SetPropertyValue";
    public const string InvokeMethod = "InvokeMethod";
    public const string GetInstanceProperties = "GetInstanceProperties";
    public const string PropertyChanged = "PropertyChanged";
    public const string RaisePropertyChanged = "RaisePropertyChanged";

    /// <summary>
    /// Emitted by both modes, not by root mode alone, which is why it is deliberately absent from
    /// <see cref="GeneratedMemberTable.RootModePlumbingMemberNames"/>: that array also answers which
    /// emitted members need a 'new' modifier, and the emitter decides that one for itself.
    /// </summary>
    public const string DefaultProperties = "DefaultProperties";
}

/// <summary>
/// The return type the contract check demands of one plumbing helper. An enum rather than a symbol,
/// because two of the four are answered from the method itself and one has to be constructed from
/// the compilation.
/// </summary>
internal enum PlumbingReturnKind
{
    OwnTypeParameter,
    Boolean,
    Object,
    PropertyMetadataDictionary
}

/// <summary>
/// The shape of one helper method root mode emits, in the single place both the contract check and
/// the hiding check read it from. Parameter types are approximated by counts plus the two positions
/// a typo really lands on, the return type and the leading string, which is enough to separate the
/// emitted signature from an unrelated overload of the same name.
/// </summary>
/// <param name="Name">The emitted member name, from <see cref="MemberNames"/>.</param>
/// <param name="TypeParameterCount">Type parameters on the emitted signature.</param>
/// <param name="ParameterCount">Value parameters on the emitted signature.</param>
/// <param name="RequiresParameterArray">
/// Consulted by the contract check only. The emitted call site uses expanded form,
/// InvokeMethod("M", lambda, p1), so a base declaring the same parameter types without params would
/// pass a signature match and then fail at the call. Hiding is the opposite: params is not part of
/// the signature C# hides by, so such a base does hide the emitted method and the 'new' modifier is
/// still required.
/// </param>
/// <param name="ReturnKind">
/// Consulted by the contract check only, never by the hiding check: C# hides by signature, which
/// excludes the return type, so a base helper returning the wrong type still hides the emitted one
/// and still needs the 'new' modifier.
/// </param>
/// <param name="RequiresLeadingString">
/// Whether the first parameter must be a string. The remaining parameters are left to the count,
/// because the emitted call site passes lambdas whose types the base would have to get wrong in a
/// way that still binds.
/// </param>
/// <param name="Declaration">How the member is named in the NI0011 message.</param>
internal sealed record PlumbingMethodShape(
    string Name,
    int TypeParameterCount,
    int ParameterCount,
    bool RequiresParameterArray,
    PlumbingReturnKind ReturnKind,
    bool RequiresLeadingString,
    string Declaration);

/// <summary>
/// What the generator emits into a subject's generated half. Both halves of the base class work read
/// this table rather than their own literals: <see cref="SubjectBaseContract"/> asks whether a base
/// exposes these members, and <see cref="SubjectMemberConflicts"/> asks whether anything on the chain
/// hides or captures them.
/// </summary>
internal static class GeneratedMemberTable
{
    public static readonly PlumbingMethodShape[] PlumbingMethods =
    [
        new PlumbingMethodShape(
            MemberNames.GetPropertyValue, TypeParameterCount: 1, ParameterCount: 2, RequiresParameterArray: false,
            PlumbingReturnKind.OwnTypeParameter, RequiresLeadingString: true,
            "protected TProperty GetPropertyValue<TProperty>(string, Func<IInterceptorSubject, TProperty>)"),
        new PlumbingMethodShape(
            MemberNames.SetPropertyValue, TypeParameterCount: 1, ParameterCount: 4, RequiresParameterArray: false,
            PlumbingReturnKind.Boolean, RequiresLeadingString: true,
            "protected bool SetPropertyValue<TProperty>(string, TProperty, TProperty, Action<IInterceptorSubject, TProperty>)"),
        new PlumbingMethodShape(
            MemberNames.InvokeMethod, TypeParameterCount: 0, ParameterCount: 3, RequiresParameterArray: true,
            PlumbingReturnKind.Object, RequiresLeadingString: true,
            "protected object? InvokeMethod(string, Func<IInterceptorSubject, object?[], object?>, params object?[])"),
        new PlumbingMethodShape(
            MemberNames.GetInstanceProperties, TypeParameterCount: 0, ParameterCount: 0, RequiresParameterArray: false,
            PlumbingReturnKind.PropertyMetadataDictionary, RequiresLeadingString: false,
            "protected IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties()")
    ];

    private static readonly string[] HijackableInterfaceMembers =
        ["Context", "Data", "SyncRoot", "AddProperties"];

    // The three arrays below each read the ones above them, and a static field initializer runs in
    // textual order, so reordering them initializes one to empty or null and takes the generator down
    // with a TypeInitializationException on every compilation. They are kept adjacent so that the
    // constraint is visible at the point where it applies.

    /// <summary>
    /// Derived from <see cref="PlumbingMethods"/> rather than repeated, so a fifth helper added there
    /// cannot be contract-checked and silently escape the hiding rule.
    /// </summary>
    public static readonly string[] GeneratedMemberNames =
        PlumbingMethods.Select(shape => shape.Name).ToArray();

    /// <summary>
    /// Every member root mode emits that a generated copy further up the chain would hide. The two
    /// INPC members are part of it because they are emitted by the same root-mode block and hide the
    /// ancestor's copies exactly like the helpers do. This is the answer for a generated ancestor,
    /// whose members have no symbol yet; <see cref="SubjectMemberConflicts.FindHiddenPlumbingMembers"/>
    /// reaches the same set member by member for a base that already exists.
    /// </summary>
    public static readonly string[] RootModePlumbingMemberNames =
        GeneratedMemberNames.Concat([MemberNames.PropertyChanged, MemberNames.RaisePropertyChanged]).ToArray();

    /// <summary>
    /// Every name the generated half occupies, in either mode: the root-mode plumbing, the
    /// IInterceptorSubject members the root implements, and the DefaultProperties both modes emit. No
    /// other emitted member may take one of these names, whatever its signature, which is the whole of
    /// what <see cref="CollidesWithGeneratedMember"/> answers for the "WithoutInterceptor" wrappers.
    /// </summary>
    private static readonly string[] GeneratedHalfMemberNames = RootModePlumbingMemberNames
        .Concat(HijackableInterfaceMembers)
        .Concat([MemberNames.DefaultProperties])
        .Distinct()
        .ToArray();

    /// <summary>
    /// The IInterceptorSubject members, paired with their interface symbols, that a member on the
    /// chain can take the slot of under interface re-implementation. Null when the interface is
    /// unreferenced, in which case nothing the generator emits would compile anyway.
    /// </summary>
    public static (string Name, ISymbol Member)[] GetHijackableInterfaceMembers(Compilation compilation)
    {
        var interfaceType = compilation.GetTypeByMetadataName(KnownTypes.IInterceptorSubject);
        if (interfaceType is null)
        {
            return [];
        }

        return HijackableInterfaceMembers
            .Select(name => (Name: name, Member: interfaceType.GetMembers(name).FirstOrDefault()))
            .Where(entry => entry.Member is not null)
            .Select(entry => (entry.Name, entry.Member!))
            .ToArray();
    }

    /// <summary>
    /// Whether a method matches the RaisePropertyChanged(string) the root emits. Read by the notify
    /// lookup, which asks whether a base already answers that call, and by the hiding check, which
    /// asks whether the emitted one needs a 'new'.
    /// </summary>
    public static bool IsRaisePropertyChangedSignature(IMethodSymbol method)
        => method.TypeParameters.Length == 0 &&
           method.Parameters.Length == 1 &&
           method.Parameters[0].RefKind == RefKind.None &&
           method.Parameters[0].Type.SpecialType == SpecialType.System_String;

    /// <summary>
    /// Whether a member the generator is about to emit under <paramref name="memberName"/> would take
    /// a name the generated half already occupies.
    /// </summary>
    /// <remarks>
    /// Deliberately name-only: any arity, no signature reasoning and no per-name exemption. Every
    /// attempt to narrow this has let a silent capture through, and a false positive here costs a
    /// rename while a capture costs the interception nobody notices is gone.
    /// The arity test was wrong because InvokeMethod ends in "params object?[]", so the emitted call
    /// sites span every arity from two upward, and an overload applicable in normal form beats the
    /// plumbing, which needs the expanded form: a two-parameter InvokeMethod wrapper swallows every
    /// generated method call and the real body never runs.
    /// The Context, Data and SyncRoot exemption was wrong because those are explicit interface
    /// properties only in a generated root. A hand-written base that satisfies the contract with
    /// public members, the shape docs/subject-guidelines.md documents, is hidden by a wrapper of that
    /// name, which is a CS0108 in a file the consumer cannot edit.
    /// The cost is a legitimate wrapper such as GetPropertyValueWithoutInterceptor(string, string),
    /// which is now reported as NI0006 and fixed by renaming.
    /// </remarks>
    public static bool CollidesWithGeneratedMember(string memberName)
        => GeneratedHalfMemberNames.Contains(memberName);
}
