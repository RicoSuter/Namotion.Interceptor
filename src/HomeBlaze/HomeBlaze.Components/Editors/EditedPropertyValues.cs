using HomeBlaze.Services;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Components.Editors;

/// <summary>
/// Applies the values of a properties editor to the properties they were edited for.
/// </summary>
public static class EditedPropertyValues
{
    /// <summary>
    /// Gets whether a property of the type must have a value, which holds for a value type that is not nullable.
    /// </summary>
    public static bool IsRequired(Type propertyType)
    {
        return propertyType.IsValueType && Nullable.GetUnderlyingType(propertyType) is null;
    }

    /// <summary>
    /// Sets every value that differs from the current value of its property. The conversions are all or nothing:
    /// nothing is set when one of the values cannot be converted to the type of its property. When a setter
    /// rejects its value, the values set before it are set back and the values after it are not set.
    /// </summary>
    /// <remarks>
    /// Setting a value back goes through the same setter. A previous value that its setter rejects as well, such
    /// as a default outside the valid range, is therefore not restored: the property keeps the edited value, which
    /// is reported as an error of its own.
    /// </remarks>
    /// <returns>
    /// One error per value that could not be converted, set or set back, which is empty when the values are applied.
    /// </returns>
    public static List<string> Apply(IEnumerable<KeyValuePair<RegisteredSubjectProperty, object?>> editedValues)
    {
        var errors = new List<string>();
        var changes = new List<(RegisteredSubjectProperty Property, object? Value, object? PreviousValue)>();

        foreach (var (property, editedValue) in editedValues)
        {
            var currentValue = property.GetValue();
            if (Equals(editedValue, currentValue))
            {
                continue;
            }

            try
            {
                changes.Add((property, ConvertTo(editedValue, property.Type), currentValue));
            }
            catch (Exception exception)
            {
                errors.Add(GetError(property, exception));
            }
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        for (var index = 0; index < changes.Count; index++)
        {
            try
            {
                changes[index].Property.SetValue(changes[index].Value);
            }
            catch (Exception exception)
            {
                errors.Add(GetError(changes[index].Property, exception));
                Restore(changes, index, errors);
                break;
            }
        }

        return errors;
    }

    private static object? ConvertTo(object? value, Type propertyType)
    {
        if (value is null)
        {
            return IsRequired(propertyType) ? throw new InvalidOperationException("A value is required.") : null;
        }

        var underlyingType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        return value.GetType() != underlyingType ? Convert.ChangeType(value, underlyingType) : value;
    }

    private static void Restore(
        List<(RegisteredSubjectProperty Property, object? Value, object? PreviousValue)> changes,
        int count,
        List<string> errors)
    {
        for (var index = count - 1; index >= 0; index--)
        {
            try
            {
                changes[index].Property.SetValue(changes[index].PreviousValue);
            }
            catch (Exception exception)
            {
                errors.Add($"Failed to restore '{changes[index].Property.GetDisplayName()}': {exception.Message}");
            }
        }
    }

    private static string GetError(RegisteredSubjectProperty property, Exception exception)
    {
        return $"Failed to set '{property.GetDisplayName()}': {exception.Message}";
    }
}
