using System.Reflection;
using System.Runtime.CompilerServices;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Whether a value of a type or of a property may be null.
/// </summary>
internal static class PropertyNullability
{
    // Weak keys, so that the properties of an unloaded plugin are not held.
    private static readonly ConditionalWeakTable<PropertyInfo, object> AcceptsNullByProperty = new();
    private static readonly object True = true;
    private static readonly object False = false;

    /// <summary>Whether the type is a reference type or a nullable value type.</summary>
    public static bool AcceptsNull(Type type) => !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

    /// <summary>
    /// Whether the property accepts null: its type is a nullable value type or a reference type that the property does not
    /// annotate as non-nullable, or any reference type in an app that cannot read annotations, such as a trimmed one.
    /// </summary>
    public static bool AcceptsNull(PropertyInfo propertyInfo) =>
        ReferenceEquals(AcceptsNullByProperty.GetValue(propertyInfo, ReadAcceptsNull), True);

    // A context per read: it is not thread-safe and caches what it reads.
    private static object ReadAcceptsNull(PropertyInfo propertyInfo)
    {
        if (propertyInfo.PropertyType.IsValueType)
        {
            return AcceptsNull(propertyInfo.PropertyType) ? True : False;
        }

        try
        {
            return new NullabilityInfoContext().Create(propertyInfo).WriteState == NullabilityState.NotNull ? False : True;
        }
        catch (InvalidOperationException)
        {
            // An app that turns off NullabilityInfoContextSupport, as trimming does by default, cannot read annotations.
            return True;
        }
    }
}
