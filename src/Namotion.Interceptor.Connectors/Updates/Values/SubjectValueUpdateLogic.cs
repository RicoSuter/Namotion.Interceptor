using System.Text.Json;

namespace Namotion.Interceptor.Connectors.Updates.Values;

/// <summary>
/// Internal logic for creating and applying value property updates.
/// </summary>
internal static class SubjectValueUpdateLogic
{
    /// <summary>
    /// Applies a value to a property update (create side).
    /// Sets Kind to Value and assigns the value.
    /// </summary>
    internal static void ApplyValueToUpdate(SubjectPropertyUpdate update, object? value)
    {
        update.Kind = SubjectPropertyUpdateKind.Value;
        update.Value = value;
    }

    /// <summary>
    /// Converts a value (potentially a JsonElement from deserialization) to the target property type.
    /// </summary>
    internal static object? ConvertValueToTargetType(object? value, Type targetType)
    {
        if (value is null)
            return null;

        if (value is not JsonElement jsonElement)
            return value;

        return jsonElement.Deserialize(targetType);
    }
}
