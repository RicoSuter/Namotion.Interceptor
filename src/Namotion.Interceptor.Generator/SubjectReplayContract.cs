using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Namotion.Interceptor.Generator;

/// <summary>
/// Resolves the generated replay contract independently of the ordinary interception contract.
/// </summary>
internal static class SubjectReplayContract
{
    public static bool CanGenerate(INamedTypeSymbol subject, Compilation compilation, CancellationToken cancellationToken)
    {
        if (HasCustomReplay(subject, compilation))
        {
            return false;
        }

        var ancestor = SubjectAncestry.FindNearestSubjectAncestor(subject);
        // No user member may capture calls to the replay helpers, including on an unmarked intermediate.
        for (var current = subject; current is not null && !SymbolEqualityComparer.Default.Equals(current, ancestor); current = current.BaseType)
        {
            if (GeneratedMemberTable.ReplayMemberNames.Any(name => current.GetMembers(name)
                .Any(member => SymbolEqualityComparer.Default.Equals(current, subject) || compilation.IsSymbolAccessibleWithin(member, subject))))
            {
                return false;
            }
        }

        if (ancestor is null)
        {
            return true;
        }

        if (SubjectAncestry.HasInterceptorSubjectAttribute(ancestor) &&
            SubjectAncestry.WillBeGeneratedInThisCompilation(ancestor, cancellationToken))
        {
            return CanGenerate(ancestor, compilation, cancellationToken);
        }

        return HasCompiledContract(ancestor, subject, compilation);
    }

    private static bool HasCustomReplay(INamedTypeSymbol subject, Compilation compilation)
    {
        var replayInterface = compilation.GetTypeByMetadataName(KnownTypes.ISubjectPropertyReplay);
        return replayInterface is not null && replayInterface.GetMembers().Any(member =>
            subject.FindImplementationForInterfaceMember(member) is { } implementation &&
            !IsGenerated(implementation));
    }

    private static bool HasCompiledContract(INamedTypeSymbol ancestor, INamedTypeSymbol subject, Compilation compilation)
    {
        var executor = FindMethod(ancestor, subject, compilation, MemberNames.GetPropertyReplayExecutor);
        var capability = FindMethod(ancestor, subject, compilation, MemberNames.CanReplayGeneratedProperty);
        var replay = FindMethod(ancestor, subject, compilation, MemberNames.ReplayGeneratedProperty);
        if (executor is not { Parameters.Length: 0 } || executor.ReturnType.ToDisplayString() != "Namotion.Interceptor.Interceptors.InterceptorExecutor")
        {
            return false;
        }

        return capability is { Parameters.Length: 1, ReturnType.SpecialType: SpecialType.System_Boolean } &&
               capability.Parameters[0] is { RefKind: RefKind.None, Type.SpecialType: SpecialType.System_String } &&
               HasReplaySignature(replay);
    }

    private static bool HasReplaySignature(IMethodSymbol? method)
    {
        if (method is not { ReturnsVoid: true, Parameters.Length: 3 })
        {
            return false;
        }

        return method.Parameters[0] is { RefKind: RefKind.None, Type.SpecialType: SpecialType.System_String } &&
               method.Parameters[1] is { RefKind: RefKind.None, Type.SpecialType: SpecialType.System_Object } &&
               method.Parameters[2].RefKind == RefKind.Ref &&
               method.Parameters[2].Type.ToDisplayString() == "Namotion.Interceptor.PropertyReplayOutcome";
    }

    private static IMethodSymbol? FindMethod(INamedTypeSymbol ancestor, INamedTypeSymbol subject, Compilation compilation, string name)
    {
        // A nearer handwritten member must not be skipped or combined with generated helpers above it.
        var member = SymbolExtensions.HidableMembers(ancestor, subject, compilation, name).FirstOrDefault();
        return member is IMethodSymbol { IsStatic: false, IsVirtual: false, IsOverride: false, TypeParameters.Length: 0 } method &&
               OwnsGeneratedReplay(method.ContainingType, compilation) ? method : null;
    }

    private static bool OwnsGeneratedReplay(INamedTypeSymbol type, Compilation compilation)
    {
        var replayInterface = compilation.GetTypeByMetadataName(KnownTypes.ISubjectPropertyReplay);
        return replayInterface is not null && replayInterface.GetMembers().All(member =>
            type.FindImplementationForInterfaceMember(member) is { } implementation &&
            SymbolEqualityComparer.Default.Equals(implementation.ContainingType, type) && IsGenerated(implementation));
    }

    private static bool IsGenerated(ISymbol member)
        => member.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.CompilerGeneratedAttribute");
}
