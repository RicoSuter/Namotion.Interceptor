namespace HomeBlaze.Virtual;

/// <summary>Maps the data types of virtual properties to and from CLR types.</summary>
public static class VirtualDataTypes
{
    /// <summary>Gets the type of the property that holds a value of the data type.</summary>
    public static Type GetClrType(VirtualDataType dataType) => dataType switch
    {
        VirtualDataType.Number => typeof(decimal?),
        VirtualDataType.Boolean => typeof(bool?),
        VirtualDataType.Text => typeof(string),
        VirtualDataType.Duration => typeof(TimeSpan?),
        VirtualDataType.DateTime => typeof(DateTimeOffset?),
        _ => throw new ArgumentOutOfRangeException(nameof(dataType), dataType, null)
    };

    /// <summary>Gets the data type that holds values of a property of the type, such as one a binding targets.</summary>
    public static bool TryGetDataType(Type type, out VirtualDataType dataType)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (VirtualValueConverter.IsNumber(underlying))
        {
            dataType = VirtualDataType.Number;
        }
        else if (underlying == typeof(bool))
        {
            dataType = VirtualDataType.Boolean;
        }
        else if (underlying == typeof(string) || underlying.IsEnum)
        {
            dataType = VirtualDataType.Text;
        }
        else if (underlying == typeof(TimeSpan))
        {
            dataType = VirtualDataType.Duration;
        }
        else if (underlying == typeof(DateTimeOffset) || underlying == typeof(DateTime))
        {
            dataType = VirtualDataType.DateTime;
        }
        else
        {
            dataType = default;
            return false;
        }

        return true;
    }
}
