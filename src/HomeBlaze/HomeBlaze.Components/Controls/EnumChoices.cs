namespace HomeBlaze.Components.Controls;

/// <summary>
/// The choices of a property that holds an enum.
/// </summary>
public static class EnumChoices
{
    /// <summary>
    /// Gets the enum type of a property type, looking through nullable, or null when it holds no enum.
    /// </summary>
    public static Type? GetEnumType(Type propertyType)
    {
        var type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        return type.IsEnum ? type : null;
    }

    /// <summary>
    /// Gets whether the values of the enum combine as flags, which a choice of one value cannot show.
    /// </summary>
    public static bool IsFlags(Type enumType) => enumType.IsDefined(typeof(FlagsAttribute), inherit: false);

    /// <summary>
    /// Gets the widget argument error of a select for a flags enum.
    /// </summary>
    public static string GetFlagsError(Type enumType) => $"Cannot select a value of the flags enum {enumType.Name}";
}
