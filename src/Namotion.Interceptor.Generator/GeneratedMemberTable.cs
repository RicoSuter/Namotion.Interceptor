using System.Linq;
using Microsoft.CodeAnalysis;

namespace Namotion.Interceptor.Generator;

/// <summary>
/// The names of the members root mode emits. A name drifting between the lookups and the emitter
/// fails silently: the emitted member loses the 'new' modifier that keeps CS0108 out of a file the
/// consumer cannot edit.
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
/// The shape of one helper method root mode emits, read by both the contract check and the hiding
/// check. Parameter types are approximated by count plus the two positions a typo really lands on,
/// which is enough to separate the emitted signature from an unrelated overload of the same name.
/// </summary>
/// <param name="Name">The emitted member name, from <see cref="MemberNames"/>.</param>
/// <param name="TypeParameterCount">Type parameters on the emitted signature.</param>
/// <param name="ParameterCount">Value parameters on the emitted signature.</param>
/// <param name="RequiresParameterArray">
/// Contract check only. The emitted call site uses expanded form, so a base declaring the same
/// parameter types without params fails at the call; hiding ignores params, so such a base does hide
/// the emitted method and still needs the 'new' modifier.
/// </param>
/// <param name="ReturnKind">
/// Contract check only, never the hiding check: C# hides by signature, which excludes the return type.
/// </param>
/// <param name="RequiresLeadingString">Whether the first parameter must be a string.</param>
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
/// What the generator emits into a subject's generated half, read from this table rather than from
/// literals repeated in each caller.
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

    // The four fields below each read the ones above them, and a static field initializer runs in
    // textual order, so reordering them leaves one null and takes the generator down with a
    // TypeInitializationException on every compilation.

    private static readonly string[] HijackableInterfaceMembers =
        ["Context", "Data", "SyncRoot", "AddProperties"];

    /// <summary>
    /// Derived from <see cref="PlumbingMethods"/> rather than repeated, so a fifth helper added there
    /// cannot be contract-checked and silently escape the hiding rule.
    /// </summary>
    public static readonly string[] GeneratedMemberNames =
        PlumbingMethods.Select(shape => shape.Name).ToArray();

    /// <summary>
    /// Every member root mode emits that a generated copy further up the chain would hide, the two
    /// INPC members included: the same root-mode block emits them and they hide the ancestor's copies
    /// exactly like the helpers do. This is the answer for a generated ancestor, whose members have no
    /// symbol yet.
    /// </summary>
    public static readonly string[] RootModePlumbingMemberNames =
        GeneratedMemberNames.Concat([MemberNames.PropertyChanged, MemberNames.RaisePropertyChanged]).ToArray();

    /// <summary>
    /// Every name the generated half occupies, in either mode. No other emitted member may take one of
    /// these names, whatever its signature.
    /// </summary>
    private static readonly string[] GeneratedHalfMemberNames = RootModePlumbingMemberNames
        .Concat(HijackableInterfaceMembers)
        .Concat([MemberNames.DefaultProperties])
        .Distinct()
        .ToArray();

    /// <summary>
    /// The IInterceptorSubject members, paired with their interface symbols, that a member on the
    /// chain can take the slot of under interface re-implementation. Empty when the interface is
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
    /// Deliberately name-only: any arity, no signature reasoning and no per-name exemption. Two
    /// attempts to narrow it, on parameter count and by exempting Context/Data/SyncRoot, each let a
    /// silent capture through. The cost is a false positive on a legitimate wrapper, reported as
    /// NI0006 and fixed by renaming. Full argument in docs/design/generator-supported-shapes.md.
    /// </remarks>
    public static bool CollidesWithGeneratedMember(string memberName)
        => GeneratedHalfMemberNames.Contains(memberName);
}
