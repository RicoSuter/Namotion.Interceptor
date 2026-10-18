using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Services;

/// <summary>
/// Utilities for converting and validating method parameters.
/// </summary>
public static class ParameterConverter
{
    private static readonly HashSet<Type> SupportedPrimitives =
    [
        typeof(string),
        typeof(int),
        typeof(long),
        typeof(double),
        typeof(float),
        typeof(decimal),
        typeof(bool),
        typeof(DateTime),
        typeof(DateTimeOffset),
        typeof(Guid),
        typeof(TimeSpan)
    ];

    private static readonly JsonSerializerOptions ObjectSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Checks if a type is supported for UI parameter input.
    /// </summary>
    public static bool IsSupported(Type type)
    {
        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;
        return SupportedPrimitives.Contains(underlyingType) || underlyingType.IsEnum;
    }

    /// <summary>
    /// Checks if a type is an object whose public settable members are all supported for UI input, so that it can be
    /// entered member by member.
    /// </summary>
    public static bool IsSupportedObject(Type type)
    {
        if (!IsJsonObject(type))
        {
            return false;
        }

        var members = GetObjectMembers(type);
        return members.Count > 0 && members.All(member => IsSupported(member.Type));
    }

    /// <summary>
    /// Checks if a value of the type can be entered as JSON: a concrete type that is no supported simple value.
    /// </summary>
    public static bool IsJsonObject(Type type)
    {
        var actualType = Nullable.GetUnderlyingType(type) ?? type;
        return !IsSupported(actualType) && actualType != typeof(object) && !actualType.IsPrimitive &&
               !actualType.IsAbstract && !actualType.IsInterface;
    }

    /// <summary>
    /// Gets the public settable members of an object type, including init-only members.
    /// </summary>
    public static IReadOnlyList<ObjectParameterMember> GetObjectMembers(Type type)
    {
        // Owned by this call: the context caches the members it reads and is not thread-safe.
        var nullabilityContext = new NullabilityInfoContext();
        return (Nullable.GetUnderlyingType(type) ?? type)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetMethod?.IsPublic == true && property.SetMethod?.IsPublic == true &&
                               property.GetIndexParameters().Length == 0 &&
                               property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition != JsonIgnoreCondition.Always)
            // A derived type may hide a same-named base member with `new`; GetProperties returns both, so keep the most derived one.
            .GroupBy(property => property.Name)
            .Select(group => group.OrderByDescending(property => GetInheritanceDepth(property.DeclaringType)).First())
            .Select(property => new ObjectParameterMember(
                property.Name,
                property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name,
                property.PropertyType,
                property.IsDefined(typeof(RequiredMemberAttribute)),
                Nullable.GetUnderlyingType(property.PropertyType) != null ||
                nullabilityContext.Create(property).WriteState == NullabilityState.Nullable))
            .ToArray();
    }

    /// <summary>
    /// Tries to create an object from the string inputs of its members, keyed by <see cref="ObjectParameterMember.Name"/>.
    /// A member without input keeps its default value; a required member without input fails.
    /// </summary>
    public static bool TryCreateObject(Type type, IReadOnlyDictionary<string, string?> inputs, out object? value, out string? error)
    {
        value = null;
        var json = new JsonObject();
        foreach (var member in GetObjectMembers(type))
        {
            var input = inputs.GetValueOrDefault(member.Name);
            if (string.IsNullOrEmpty(input))
            {
                if (member.IsRequired)
                {
                    error = $"{member.Name} is required";
                    return false;
                }

                continue;
            }

            if (!TryConvert(input, member.Type, out var memberValue))
            {
                error = $"Invalid value for {member.Name}";
                return false;
            }

            json[member.JsonName] = JsonSerializer.SerializeToNode(memberValue, member.Type, ObjectSerializerOptions);
        }

        return TryDeserialize(json.ToJsonString(), type, out value, out error);
    }

    /// <summary>
    /// Tries to read an object from JSON text. Member names match in any case and enums are read by name. Empty text
    /// is null, which only a nullable parameter accepts.
    /// </summary>
    public static bool TryParseJson(string? input, Type type, bool isNullable, out object? value, out string? error)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            value = null;
            error = isNullable ? null : "This field is required";
            return isNullable;
        }

        return TryDeserialize(input, type, out value, out error);
    }

    private static bool TryDeserialize(string json, Type type, out object? value, out string? error)
    {
        try
        {
            value = JsonSerializer.Deserialize(json, type, ObjectSerializerOptions);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            value = null;
            error = exception.Message;
            return false;
        }
    }

    private static int GetInheritanceDepth(Type? type)
    {
        var depth = 0;
        while (type is not null)
        {
            depth++;
            type = type.BaseType;
        }

        return depth;
    }

    /// <summary>
    /// Tries to convert a string input to the value of a parameter with the given unit. A
    /// <see cref="StateUnit.Percent"/> input is entered from 0 to 100 and returned as the fraction from 0 to 1 the
    /// parameter expects; integer types cannot hold a fraction and are returned unscaled.
    /// </summary>
    public static bool TryConvert(string? input, Type targetType, StateUnit? unit, out object? value)
    {
        if (!TryConvert(input, targetType, out value))
        {
            return false;
        }

        if (unit != StateUnit.Percent)
        {
            return true;
        }

        try
        {
            // Through decimal, so 33.3 becomes 0.333 and not 0.33299999999999996.
            value = value switch
            {
                decimal percent => percent / 100m,
                double percent => (double)((decimal)percent / 100m),
                float percent => (float)((decimal)percent / 100m),
                _ => value
            };
            return true;
        }
        catch (OverflowException)
        {
            value = null;
            return false;
        }
    }

    /// <summary>
    /// Tries to convert a string input to the target type.
    /// </summary>
    public static bool TryConvert(string? input, Type targetType, out object? value)
    {
        value = null;

        var underlyingType = Nullable.GetUnderlyingType(targetType);
        var isNullable = underlyingType != null;
        var actualType = underlyingType ?? targetType;

        // Handle null/empty for nullable types
        if (string.IsNullOrEmpty(input))
        {
            if (isNullable || actualType == typeof(string))
            {
                value = null;
                return true;
            }
            return false;
        }

        try
        {
            // Handle enums
            if (actualType.IsEnum)
            {
                if (Enum.TryParse(actualType, input, ignoreCase: true, out var enumValue))
                {
                    value = enumValue;
                    return true;
                }
                return false;
            }

            // Handle specific types
            if (actualType == typeof(string))
            {
                value = input;
                return true;
            }

            if (actualType == typeof(bool))
            {
                if (bool.TryParse(input, out var boolValue))
                {
                    value = boolValue;
                    return true;
                }
                return false;
            }

            if (actualType == typeof(Guid))
            {
                if (Guid.TryParse(input, out var guidValue))
                {
                    value = guidValue;
                    return true;
                }
                return false;
            }

            if (actualType == typeof(TimeSpan))
            {
                if (TimeSpan.TryParse(input, CultureInfo.InvariantCulture, out var timeSpanValue))
                {
                    value = timeSpanValue;
                    return true;
                }
                return false;
            }

            // Use TypeConverter for numeric types and DateTime
            var converter = TypeDescriptor.GetConverter(actualType);
            if (converter.CanConvertFrom(typeof(string)))
            {
                value = converter.ConvertFromInvariantString(input);
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
