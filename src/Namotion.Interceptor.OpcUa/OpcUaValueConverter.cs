using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa;

public class OpcUaValueConverter
{
    /// <summary>
    /// Converts an OPC UA node value to the CLR property type while handling.
    /// </summary>
    public virtual object? ConvertToPropertyValue(object? nodeValue, RegisteredSubjectProperty property)
    {
        if (nodeValue is null)
        {
            return null;
        }

        var propertyType = property.Type;
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
                var result = new decimal[doubleArray.Length];
                for (var i = 0; i < doubleArray.Length; i++)
                {
                    result[i] = (decimal)doubleArray[i];
                }
                return result;
            }
        }

        return nodeValue;

    }

    /// <summary>
    /// Converts a CLR property value to an OPC UA compatible value.
    /// </summary>
    public virtual object? ConvertToNodeValue(object? propertyValue, RegisteredSubjectProperty property)
    {
        var propertyType = property.Type;
        var type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (type == typeof(decimal))
        {
            return propertyValue is decimal dv ? (double)dv : propertyValue;
        }

        if (type.IsArray)
        {
            var elementType = type.GetElementType()!;
            if (propertyValue is Array array)
            {
                if (elementType == typeof(decimal))
                {
                    var decimals = (decimal[])array;
                    return decimals.Select(d => (double)d).ToArray();
                }

                return propertyValue;
            }

            return Array.CreateInstance(elementType, 0); // value is null => empty array
        }

        return propertyValue;
    }
    
    /// <summary>
    /// Converts a CLR property value to an OPC UA compatible value + data type.
    /// </summary>
    public virtual TypeInfo GetNodeTypeInfo(Type propertyType)
    {
        var type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (type == typeof(decimal))
        {
            return TypeInfo.Construct(typeof(double));
        }

        if (type.IsArray && type.GetElementType() == typeof(decimal))
        {
            return TypeInfo.Construct(typeof(double[]));
        }

        return TypeInfo.Construct(type);
    }
}