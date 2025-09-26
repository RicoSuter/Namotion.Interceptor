namespace Namotion.Interceptor.OpcUa;

public class OpcUaDataValueConverter
{
    /// <summary>
    /// Converts an OPC UA node value to the CLR property type while handling.
    /// </summary>
    public virtual object? ConvertToPropertyValue(object? nodeValue, Type propertyType)
    {
        if (nodeValue is null)
        {
            return null;
        }

        var targetType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (targetType == typeof(decimal))
        {
            if (nodeValue is not decimal)
            {
                nodeValue = Convert.ToDecimal(nodeValue);
            }
            return nodeValue;
        }

        if (targetType.IsArray)
        {
            var targetElement = targetType.GetElementType()!;
            if (targetType.GetArrayRank() == 1 && 
                targetElement == typeof(decimal) && 
                nodeValue is double[] doubleArray)
            {
                return doubleArray.Select(d => (decimal)d).ToArray();
            }
        }

        return nodeValue;

    }

    /// <summary>
    /// Converts a CLR property value to an OPC UA compatible value + data type.
    /// </summary>
    public virtual (object? value, Type nodeType) ConvertToNodeValue(object? propertyValue, Type propertyType)
    {
        var type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (type == typeof(decimal))
        {
            return (propertyValue is decimal dv ? (double)dv : propertyValue, typeof(double));
        }

        if (type.IsArray)
        {
            var elementType = type.GetElementType()!;
        
            if (propertyValue is Array array)
            {
                if (elementType == typeof(decimal))
                {
                    var decimals = (decimal[])array;
                    var doubles = decimals.Select(d => (double)d).ToArray();
                    return (doubles, typeof(double[]));
                }

                return (propertyValue, type);
            }

            return (Array.CreateInstance(elementType, 0), elementType.MakeArrayType());
        }

        return (propertyValue, type);
    }
}