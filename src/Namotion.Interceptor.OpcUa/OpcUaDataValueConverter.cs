using System.Collections;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa;

public class OpcUaDataValueConverter
{
    public object? ConvertToPropertyValue(object? nodeValue, Type propertyType)
    {
        var targetType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (targetType == typeof(decimal))
        {
            if (nodeValue is not null)
            {
                nodeValue = Convert.ToDecimal(nodeValue);
            }
        }
        else if (propertyType.IsArray && propertyType.GetElementType() == typeof(decimal))
        {
            if (nodeValue is double[] doubleArray)
            {
                nodeValue = doubleArray.Select(d => (decimal)d).ToArray();
            }
        }

        return nodeValue;
    }

    public (object?, Type) ConvertToNodeValue(object? propertyValue, Type propertyType)
    {
        var type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        // Decimal -> double for OPC UA
        if (type == typeof(decimal))
        {
            type = typeof(double);
            if (propertyValue is decimal dv)
            {
                propertyValue = (double)dv;
            }
        }

        // Normalize IEnumerable<T> to T[] and handle decimal element type
        var enumerableInterface = type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (!type.IsArray && enumerableInterface != null && type != typeof(string))
        {
            var elementType = enumerableInterface.GetGenericArguments()[0];
            var targetElementType = elementType == typeof(decimal) ? typeof(double) : elementType;

            // Convert value to array of targetElementType
            if (propertyValue is IEnumerable enumerable)
            {
                var list = new List<object?>();
                foreach (var item in enumerable)
                {
                    if (item is null)
                    {
                        list.Add(null);
                    }
                    else if (elementType == typeof(decimal))
                    {
                        list.Add(Convert.ToDouble(item));
                    }
                    else
                    {
                        list.Add(item);
                    }
                }

                var convertedArray = Array.CreateInstance(targetElementType, list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    convertedArray.SetValue(list[i], i);
                }
                propertyValue = convertedArray;
                type = targetElementType.MakeArrayType();
            }
            else
            {
                // No value yet -> create empty array
                type = targetElementType.MakeArrayType();
                propertyValue = Array.CreateInstance(targetElementType, 0);
            }
        }
        else if (type.IsArray)
        {
            var elementType = type.GetElementType()!;
            if (propertyValue == null)
            {
                // Create empty array
                var targetElementType = elementType == typeof(decimal) ? typeof(double) : elementType;
                propertyValue = Array.CreateInstance(targetElementType, 0);
                type = targetElementType.MakeArrayType();
            }
            else if (elementType == typeof(decimal))
            {
                // decimal[] -> double[]
                var decimalArray = (decimal[])propertyValue;
                propertyValue = decimalArray.Select(d => (double)d).ToArray();
                type = typeof(double[]);
            }
            else if (elementType.IsArray)
            {
                // Jagged arrays -> object[] for OPC UA compatibility
                if (propertyValue is Array jaggedArray)
                {
                    var objectArray = new object[jaggedArray.Length];
                    for (int i = 0; i < jaggedArray.Length; i++)
                    {
                        objectArray[i] = jaggedArray.GetValue(i)!;
                    }
                    propertyValue = objectArray;
                    type = typeof(object[]);
                }
            }
        }

        return (propertyValue, type);
    }
}