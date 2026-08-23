using Microsoft.CodeAnalysis;

namespace Namotion.Interceptor.Generator;

/// <summary>
/// Decides at generation time whether a property setter routes through the synchronized
/// structural accessor helper or the plain scalar one.
/// </summary>
/// <remarks>
/// The decision fails closed: the scalar route is emitted only for declared types that provably
/// cannot hold a subject, and everything else takes the structural route. Reproducing the runtime
/// classifier against Roslyn symbols is not possible here, because the generator emits the
/// IInterceptorSubject base-list entry itself, so a subject symbol from the same compilation does
/// not carry the interface; dynamic, unresolved types and multi-dimensional arrays diverge from
/// their runtime classification as well. The asymmetry is what makes failing closed correct: a
/// false structural positive costs one predictable branch on an uncommon property, while a false
/// scalar negative silently skips the pre-chain synchronization seam (see
/// IInterceptorExecutor.SetStructuralPropertyValue) on exactly the path it exists for, even
/// though the lifecycle still does structural work because it classifies from runtime metadata.
/// </remarks>
internal static class PropertyWriteRouting
{
    /// <summary>
    /// Whether the setter for a property of <paramref name="declaredType"/> must take the
    /// structural route. A null symbol is an unresolved type and routes structurally.
    /// </summary>
    public static bool RequiresStructuralWrite(ITypeSymbol? declaredType, Compilation compilation)
        => !IsProvablySubjectFree(declaredType, compilation);

    /// <summary>
    /// The scalar allowlist: the built-in primitives, string, decimal, DateTime, DateTimeOffset,
    /// TimeSpan, Guid, enums, and Nullable&lt;T&gt; of any of these. Every entry is a type the
    /// runtime classifier can never see a subject behind.
    /// </summary>
    private static bool IsProvablySubjectFree(ITypeSymbol? type, Compilation compilation)
    {
        if (type is null)
        {
            return false;
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            return true;
        }

        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Char:
            case SpecialType.System_SByte:
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
            case SpecialType.System_String:
            case SpecialType.System_DateTime:
            case SpecialType.System_IntPtr:
            case SpecialType.System_UIntPtr:
                return true;
        }

        if (type is not INamedTypeSymbol namedType)
        {
            return false;
        }

        if (namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            return IsProvablySubjectFree(namedType.TypeArguments[0], compilation);
        }

        // No SpecialType exists for these three, so they are compared against the compilation's own
        // symbol rather than by display string: a user-defined type of the same name must not slip
        // onto the scalar route.
        return IsWellKnownScalarStruct(namedType, compilation, "System.DateTimeOffset") ||
               IsWellKnownScalarStruct(namedType, compilation, "System.TimeSpan") ||
               IsWellKnownScalarStruct(namedType, compilation, "System.Guid");
    }

    private static bool IsWellKnownScalarStruct(INamedTypeSymbol type, Compilation compilation, string metadataName)
    {
        var wellKnownType = compilation.GetTypeByMetadataName(metadataName);
        return wellKnownType is not null &&
               SymbolEqualityComparer.Default.Equals(type, wellKnownType);
    }
}
