namespace HomeBlaze.History.Abstractions;

/// <summary>
/// The typed column a value is routed into.
/// </summary>
public enum ValueColumn
{
    /// <summary>Integer types and bool (bool as 0/1).</summary>
    Long,

    /// <summary>double and float.</summary>
    Double,

    /// <summary>decimal, string, enum, and (v1.1) path references.</summary>
    Json
}

/// <summary>
/// Single source of truth for routing a value into a column (write) and building
/// column-targeted SQL (read).
/// </summary>
public static class HistoryColumns
{
    /// <summary>
    /// Returns the column a value of <paramref name="propertyType"/> is stored in.
    /// </summary>
    public static ValueColumn GetValueColumnFor(Type propertyType)
    {
        var t = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (t == typeof(double) || t == typeof(float)) return ValueColumn.Double;
        if (IsBigIntCompatible(t)) return ValueColumn.Long;
        return ValueColumn.Json; // decimal, string, enum, (v1.1) path references
    }

    /// <summary>
    /// Returns true for ulong (or ulong?) properties. ulong values above long.MaxValue spill
    /// to value_json; read paths COALESCE across both columns.
    /// </summary>
    public static bool IsUlongProperty(Type propertyType) =>
        (Nullable.GetUnderlyingType(propertyType) ?? propertyType) == typeof(ulong);

    /// <summary>
    /// Returns true for integer types and bool, which all store losslessly in value_long.
    /// Shared by column dispatch and eligibility so both agree on what lands in value_long.
    /// </summary>
    internal static bool IsBigIntCompatible(Type t) =>
        t == typeof(long) || t == typeof(int) || t == typeof(short) ||
        t == typeof(sbyte) || t == typeof(byte) || t == typeof(ushort) ||
        t == typeof(uint) || t == typeof(ulong) || t == typeof(bool);
}
