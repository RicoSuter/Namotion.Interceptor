namespace Namotion.Interceptor.Cache;

/// <summary>
/// Decides whether the runtime reads and writes a value of a given type atomically, so that a plain
/// read racing a write can never observe a partially written value.
/// </summary>
internal static class AtomicAccess
{
    /// <summary>
    /// True for the types whose loads and stores the .NET memory model defines as atomic: object
    /// references, and primitive or enum values no wider than a pointer. Every other value type is
    /// copied without that guarantee, including single-field wrappers and <see cref="Nullable{T}"/>.
    /// </summary>
    public static bool IsGuaranteedFor(Type type)
    {
        if (!type.IsValueType)
        {
            return true;
        }

        if (!type.IsPrimitive && !type.IsEnum)
        {
            return false;
        }

        // An enum reports the type code of its underlying integral type.
        switch (Type.GetTypeCode(type))
        {
            case TypeCode.Int64:
            case TypeCode.UInt64:
            case TypeCode.Double:
                return IntPtr.Size >= sizeof(long);

            default:
                // Four bytes or fewer, or IntPtr and UIntPtr, which are pointer-sized by definition.
                return true;
        }
    }
}
