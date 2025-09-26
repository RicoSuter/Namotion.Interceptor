using Opc.Ua;

namespace Namotion.Interceptor.OpcUa;

public class OpcUaDataValueConversion
{
    public static object? ConvertToPropertyValue(DataValue dataValue, Type type)
    {
        var value = dataValue.Value;

        var targetType = Nullable.GetUnderlyingType(type) ?? type;
        if (targetType == typeof(decimal))
        {
            if (value is not null)
            {
                value = Convert.ToDecimal(value);
            }
        }
        else if (type.IsArray && type.GetElementType() == typeof(decimal))
        {
            if (value is double[] doubleArray)
            {
                value = doubleArray.Select(d => (decimal)d).ToArray();
            }
        }

        return value;
    }
}