using HomeBlaze.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.History.Abstractions;

/// <summary>
/// Single source of truth for whether a property is recorded and whether the UI offers the
/// history action. Type-based, and reads the HomeBlaze registry attribute (there is no
/// IsState member on <see cref="RegisteredSubjectProperty"/>: "is state" is the presence of
/// the HB:State attribute, and "has children" is <see cref="RegisteredSubjectProperty.CanContainSubjects"/>).
/// </summary>
public static class HistoryEligibility
{
    /// <summary>
    /// Returns true if the property is a recordable scalar [State] property.
    /// </summary>
    public static bool HasHistory(this RegisteredSubjectProperty property)
    {
        if (property.TryGetAttribute(KnownAttributes.State) is null) return false; // not [State]
        if (property.CanContainSubjects) return false;                             // structural (v1.1)
        return IsRecordableType(property.Type);
    }

    /// <summary>
    /// Returns true if a value of <paramref name="type"/> can be recorded as a scalar sample.
    /// Complex types are deferred.
    /// </summary>
    public static bool IsRecordableType(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(double) || t == typeof(float)) return true;     // value_double
        if (HistoryColumns.IsBigIntCompatible(t)) return true;          // value_long
        if (t == typeof(decimal)) return true;                          // value_json (lossless)
        if (t == typeof(string)) return true;                           // value_json
        if (t.IsEnum) return true;                                      // value_json (enum name)
        return false;                                                   // complex types deferred
    }
}
