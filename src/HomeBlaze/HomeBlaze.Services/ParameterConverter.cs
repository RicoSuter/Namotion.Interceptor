using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;

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
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    // Weak, so that the types of an unloaded plugin are not kept alive.
    private static readonly ConditionalWeakTable<Type, ObjectParameterMember[]> ObjectMembersByType = new();

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
    /// entered member by member. A collection or array is excluded even though its members (such as
    /// <see cref="List{T}.Capacity"/>) might otherwise qualify; it stays enterable as JSON through
    /// <see cref="IsJsonObject"/>.
    /// </summary>
    public static bool IsSupportedObject(Type type)
    {
        if (!IsJsonObject(type))
        {
            return false;
        }

        var actualType = Nullable.GetUnderlyingType(type) ?? type;
        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(actualType))
        {
            return false;
        }

        var members = GetObjectMembers(type);
        return members.Count > 0 && members.All(member => IsSupported(member.Type));
    }

    /// <summary>
    /// Checks if a value of the type can be entered as JSON: a concrete, non-delegate type that is no supported
    /// simple value.
    /// </summary>
    public static bool IsJsonObject(Type type)
    {
        var actualType = Nullable.GetUnderlyingType(type) ?? type;
        return !IsSupported(actualType) && actualType != typeof(object) && !actualType.IsPrimitive &&
               !actualType.IsAbstract && !actualType.IsInterface && !typeof(Delegate).IsAssignableFrom(actualType) &&
               !typeof(IInterceptorSubject).IsAssignableFrom(actualType);
    }

    /// <summary>
    /// Gets the public settable members of an object type, including init-only members. The result is cached per type.
    /// </summary>
    public static IReadOnlyList<ObjectParameterMember> GetObjectMembers(Type type) =>
        ObjectMembersByType.GetValue(Nullable.GetUnderlyingType(type) ?? type, BuildObjectMembers);

    private static ObjectParameterMember[] BuildObjectMembers(Type type)
    {
        return type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetMethod?.IsPublic == true && property.SetMethod?.IsPublic == true &&
                               property.GetIndexParameters().Length == 0 &&
                               property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition != JsonIgnoreCondition.Always)
            // Keep these member rules in sync with McpMethodParameter.CreatePropertiesCore in Namotion.Interceptor.Mcp.
            .GroupBy(property => property.Name)
            .Select(group => group.Aggregate((best, next) => next.DeclaringType!.IsSubclassOf(best.DeclaringType!) ? next : best))
            .Select(property => new ObjectParameterMember(
                property.Name,
                property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name,
                property.PropertyType,
                property.IsDefined(typeof(RequiredMemberAttribute))))
            .ToArray();
    }

    /// <summary>
    /// Tries to create an object from the string inputs of its members, keyed by <see cref="ObjectParameterMember.Name"/>.
    /// A member without input keeps its default value; a required member without input fails, except a required
    /// non-nullable <see cref="bool"/> member, whose unticked default (false) is written explicitly instead, since
    /// a checkbox never actually omits a value. When <paramref name="isNullable"/> and every member has no input,
    /// treating an unticked or empty <see cref="bool"/> member the same as no input, the object itself was not
    /// provided and this returns true with a null value instead of applying members' defaults.
    /// </summary>
    public static bool TryCreateObject(
        Type type, IReadOnlyDictionary<string, string?> inputs, bool isNullable, out object? value, out string? error)
    {
        value = null;
        var members = GetObjectMembers(type);
        if (isNullable && members.All(member => IsEffectivelyEmpty(member, inputs.GetValueOrDefault(member.Name))))
        {
            error = null;
            return true;
        }

        var json = new JsonObject();
        foreach (var member in members)
        {
            var input = inputs.GetValueOrDefault(member.Name);
            if (string.IsNullOrEmpty(input))
            {
                if (member.IsRequired)
                {
                    if (member.Type == typeof(bool))
                    {
                        // System.Text.Json rejects a required member that is missing from the JSON.
                        json[member.JsonName] = false;
                        continue;
                    }

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

            try
            {
                json[member.JsonName] = JsonSerializer.SerializeToNode(memberValue, member.Type, ObjectSerializerOptions);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                error = $"Invalid value for {member.Name}";
                return false;
            }
        }

        return TryDeserialize(json, static (node, type) => node.Deserialize(type, ObjectSerializerOptions), type, out value, out error);
    }

    private static bool IsEffectivelyEmpty(ObjectParameterMember member, string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return true;
        }

        var underlyingType = Nullable.GetUnderlyingType(member.Type) ?? member.Type;
        return underlyingType == typeof(bool) && string.Equals(input, "False", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tries to read an object from JSON text. Member names match in any case and enums are read by name. Empty text,
    /// and a JSON <c>null</c> literal, are null, which only a nullable parameter accepts.
    /// </summary>
    public static bool TryParseJson(string? input, Type type, bool isNullable, out object? value, out string? error)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            value = null;
            error = isNullable ? null : "This field is required";
            return isNullable;
        }

        if (!TryDeserialize(input, static (text, type) => JsonSerializer.Deserialize(text, type, ObjectSerializerOptions), type, out value, out error))
        {
            return false;
        }

        if (value is null && !isNullable)
        {
            error = "This field is required";
            return false;
        }

        return true;
    }

    private static bool TryDeserialize<TJson>(
        TJson json, Func<TJson, Type, object?> deserialize, Type type, out object? value, out string? error)
    {
        try
        {
            value = deserialize(json, type);
            error = null;
            return true;
        }
        // Setters and constructors run during deserialization and may throw anything; a Try method must not let it escape.
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            value = null;
            error = exception.Message;
            return false;
        }
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
