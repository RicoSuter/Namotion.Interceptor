using System.Reflection;
using System.Runtime.CompilerServices;

namespace HomeBlaze.Services;

/// <summary>
/// Whether a type, a property or a parameter may be null, as read from its type and its nullable annotations.
/// </summary>
public static class MemberNullability
{
    // Weak keys, so that the properties of an unloaded plugin are not held.
    private static readonly ConditionalWeakTable<PropertyInfo, object> AcceptsNullByProperty = new();
    private static readonly object True = true;
    private static readonly object False = false;

    // False when the app turns off NullabilityInfoContextSupport, as trimming does by default.
    private static readonly bool IsNullabilityInfoSupported =
        !AppContext.TryGetSwitch("System.Reflection.NullabilityInfoContext.IsSupported", out var isSupported) || isSupported;

    /// <summary>Whether the type is a reference type or a nullable value type.</summary>
    public static bool AcceptsNull(Type type) => !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

    /// <summary>
    /// Whether the property accepts null: its type is a nullable value type or a reference type that the property does not
    /// annotate as non-nullable, or any reference type in an app that cannot read annotations, such as a trimmed one.
    /// </summary>
    public static bool AcceptsNull(PropertyInfo property) =>
        ReferenceEquals(AcceptsNullByProperty.GetValue(property, ReadAcceptsNull), True);

    /// <summary>
    /// Creates a context to read the annotations of members with, or returns null in an app that cannot read annotations,
    /// such as a trimmed one.
    /// </summary>
    /// <remarks>The context is not thread-safe and keeps every member it reads alive, so use one per computation rather than a shared one.</remarks>
    public static NullabilityInfoContext? TryCreateContext() =>
        IsNullabilityInfoSupported ? new NullabilityInfoContext() : null;

    /// <summary>
    /// Gets the write state that the parameter is annotated with, or <see cref="NullabilityState.Unknown"/> when
    /// <paramref name="context"/> is null or the annotations cannot be read.
    /// </summary>
    /// <param name="context">The context from <see cref="TryCreateContext"/>.</param>
    /// <param name="parameter">The parameter.</param>
    public static NullabilityState GetWriteState(NullabilityInfoContext? context, ParameterInfo parameter)
    {
        if (context is null)
        {
            return NullabilityState.Unknown;
        }

        try
        {
            return context.Create(parameter).WriteState;
        }
        catch (InvalidOperationException)
        {
            // For support turned off in a way that the switch does not show.
            return NullabilityState.Unknown;
        }
    }

    // A context per read: it is not thread-safe and caches what it reads.
    private static object ReadAcceptsNull(PropertyInfo property)
    {
        if (property.PropertyType.IsValueType)
        {
            return AcceptsNull(property.PropertyType) ? True : False;
        }

        return GetWriteState(TryCreateContext(), property) == NullabilityState.NotNull ? False : True;
    }

    private static NullabilityState GetWriteState(NullabilityInfoContext? context, PropertyInfo property)
    {
        if (context is null)
        {
            return NullabilityState.Unknown;
        }

        try
        {
            return context.Create(property).WriteState;
        }
        catch (InvalidOperationException)
        {
            // For support turned off in a way that the switch does not show.
            return NullabilityState.Unknown;
        }
    }
}
